using System.Text.Json;
using CodeSearch.Core.Semantics;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class SemanticImpactExplanationTests
{
    [Fact]
    public async Task Model_selects_only_a_review_question_and_never_supplies_the_fact_or_citation()
    {
        var report = Report();
        var broker = new ReplyBroker(payload => JsonSerializer.Serialize(new Dictionary<string, string>
            { [report.Evidence[0].Id] = "usage-contract" }));
        var result = await new SemanticImpactExplanation(() => broker).ExplainAsync(() => report,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(report, result.Report);
        var explanation = JsonSerializer.SerializeToElement(result.Explanation);
        Assert.Equal("complete", explanation.GetProperty("status").GetString());
        var review = Assert.Single(explanation.GetProperty("reviews").EnumerateArray());
        Assert.Equal(report.Evidence[0].Id, review.GetProperty("evidenceId").GetString());
        Assert.Equal("usage-contract", review.GetProperty("questionCode").GetString());
        Assert.Equal("heuristic", review.GetProperty("questionVerification").GetString());
        Assert.Equal("model-used", explanation.GetProperty("model").GetString());
        var request = Assert.Single(broker.Requests);
        var payload = Assert.IsType<ChatJobPayload>(request.Payload);
        Assert.Equal(LocalTaskProfile.Classification, payload.TaskProfile);
        Assert.Contains("<untrusted-content", payload.Prompt, StringComparison.Ordinal);
        Assert.Equal(report.Evidence[0].Id, Assert.Single(payload.OutputSchema!.Fields).Name);
        Assert.DoesNotContain("override-contract", payload.OutputSchema.Fields[0].AllowedValues!);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"invented-evidence\":\"usage-contract\"}")]
    [InlineData("```json\n{}\n```")]
    [InlineData("{\"unfinished\":")]
    [InlineData("\"\"")]
    public async Task Incomplete_or_unbound_model_answers_are_never_delivered_as_explanations(string answer)
    {
        var broker = new ReplyBroker(_ => answer);
        using var usage = new UsageCall(UsageSurface.Cli, "localai", "semantic impact");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new SemanticImpactExplanation(() => broker)
            .ExplainAsync(Report, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(UsageValidation.Failed, usage.Complete().Outcome.Validation);
        Assert.Equal(UsageTechnicalOutcome.Refused, usage.Complete().Outcome.Technical);
    }

    [Fact]
    public async Task A_valid_json_choice_for_the_wrong_relationship_is_rejected()
    {
        var report = Report();
        var broker = new ReplyBroker(_ => JsonSerializer.Serialize(new Dictionary<string, string>
            { [report.Evidence[0].Id] = "override-contract" }));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new SemanticImpactExplanation(() => broker)
            .ExplainAsync(() => report, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_snapshot_is_rechecked_after_the_model_finishes()
    {
        var report = Report();
        var reads = 0;
        SemanticImpactReport Read() => ++reads == 1 ? report : report with { TotalEvidence = 2 };
        var broker = new ReplyBroker(_ => JsonSerializer.Serialize(new Dictionary<string, string>
            { [report.Evidence[0].Id] = "test-coverage" }));
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new SemanticImpactExplanation(() => broker)
            .ExplainAsync(Read, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("impact_snapshot_changed", error.Message);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Cancellation_is_carried_into_the_broker_and_no_result_is_delivered()
    {
        using var cancelled = new CancellationTokenSource();
        var broker = new ReplyBroker(_ => { cancelled.Cancel(); return "{}"; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SemanticImpactExplanation(() => broker)
            .ExplainAsync(Report, cancellationToken: cancelled.Token));
        Assert.Equal(cancelled.Token, broker.Token);
    }

    [Fact]
    public async Task Empty_and_oversize_graphs_do_not_start_a_broker()
    {
        var service = new SemanticImpactExplanation(() => throw new InvalidOperationException("must not start"));
        var empty = Report() with { Evidence = [], TotalEvidence = 0 };
        var result = await service.ExplainAsync(() => empty, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("no-evidence", JsonSerializer.SerializeToElement(result.Explanation).GetProperty("status").GetString());
        var report = Report();
        var large = report with { Evidence = Enumerable.Range(0, 17).Select(i => report.Evidence[0] with { Id = i.ToString() }).ToArray(), TotalEvidence = 17 };
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ExplainAsync(() => large,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("impact_explanation_limit", error.Message);
    }

    private static SemanticImpactReport Report() => new(new("repo", "generation", "tree", null), "dotnet T:A", [],
        [new(new string('a', 64), "reference", "dotnet T:A", null,
            new(new("Use.cs", new(2, 3, 2, 4), "dotnet T:A", SemanticOccurrenceRoles.Reference, NavigationPrecision.Precise), new string('b', 64)))], 1);

    [Theory]
    [InlineData("reference", "usage-contract")]
    [InlineData("implementation", "implementation-contract")]
    [InlineData("override", "override-contract")]
    [InlineData("type-definition", "type-contract")]
    [InlineData("reference", "insufficient-context")]
    public async Task Each_relation_has_bound_questions_and_partial_coverage_stays_explicit(string kind, string question)
    {
        var original = Report();
        var report = original with { Evidence = [original.Evidence[0] with { Kind = kind }], TotalEvidence = 3 };
        var broker = new ReplyBroker(payload => JsonSerializer.Serialize(payload.OutputSchema!.Fields.ToDictionary(field => field.Name, _ => question)));
        var result = await new SemanticImpactExplanation(() => broker).ExplainAsync(() => report, cancellationToken: TestContext.Current.CancellationToken);
        var data = JsonSerializer.SerializeToElement(result.Explanation);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, data.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(3, data.GetProperty("totalEvidence").GetInt32());
        Assert.Equal(question, data.GetProperty("reviews")[0].GetProperty("questionCode").GetString());
        Assert.DoesNotContain("ImpactFact_", data.GetProperty("reviews")[0].GetProperty("relationship").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversize_context_is_refused_before_inference_and_duplicate_selections_are_rejected()
    {
        var original = Report();
        var large = original with { Evidence = [original.Evidence[0] with { TargetSymbolId = new string('x', 16_001) }] };
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            new SemanticImpactExplanation(() => throw new InvalidOperationException("must not start"))
                .ExplainAsync(() => large, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("impact_explanation_budget", error.Message);
        var broker = new ReplyBroker(payload => "{\"" + payload.OutputSchema!.Fields[0].Name + "\":\"test-coverage\",\"" +
            payload.OutputSchema.Fields[0].Name + "\":\"usage-contract\"}");
        var rejected = await Assert.ThrowsAsync<ImpactExplanationRejectedException>(() => new SemanticImpactExplanation(() => broker)
            .ExplainAsync(() => original, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(rejected.Validation.Errors, error => error.Code == "duplicate_field");
    }

    internal sealed class ReplyBroker(Func<ChatJobPayload, string> answer) : IBrokerClient
    {
        public List<LocalJobRequest> Requests { get; } = [];
        public CancellationToken Token { get; private set; }
        public Task<LocalJobResult<T>> ExecuteAsync<T>(LocalJobRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request); Token = cancellationToken;
            var content = answer(Assert.IsType<ChatJobPayload>(request.Payload));
            return Task.FromResult(new LocalJobResult<T>((T)(object)new ChatJobOutput(content),
                new(request.JobId, "test", "chat", "model-used", TimeSpan.Zero, TimeSpan.Zero, 20, 0, null, null, null)));
        }
    }
}
