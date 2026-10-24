using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Mcp;
using LocalLm.Core;
using ModelContextProtocol.Protocol;

namespace LocalLm.Tests;

public sealed partial class LocalTasksTests
{
    [Theory]
    [InlineData("{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1},\"buildSummaryErrors\":[],\"extra\":true}")]
    [InlineData("{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1,\"CS1002\":1},\"buildSummaryErrors\":[]}")]
    public async Task A_focused_envelope_cannot_bypass_strict_compiler_inventory_validation(string invalidInventory)
    {
        var envelope = "{\"inventory\":" + invalidInventory + ",\"requestedAnswer\":\"Requested test row\"}";
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = envelope }).TriageLogAsync(
            path: null, text: "source.cs(1,1): error CS1002: ; expected", question: "Only the final test row.",
            model: null, TestContext.Current.CancellationToken);
        Assert.NotNull(result.DiagnosticVerification);
        Assert.Equal("failed", result.DiagnosticVerification.Status);
        Assert.Equal(1, result.CompilerDiagnostics!.DiagnosticLineCount);
    }

    [Theory]
    [InlineData(",\"requestedAnswer\":\"UNACCEPTED_DUPLICATE\"")]
    [InlineData(",\"extra\":true")]
    public async Task Invalid_focused_envelope_fields_are_not_rendered_as_a_requested_answer(string extraField)
    {
        const string inventory = "{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1},\"buildSummaryErrors\":[]}";
        var envelope = "{\"inventory\":" + inventory + ",\"requestedAnswer\":\"UNACCEPTED_ANSWER\"" + extraField + "}";
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = envelope }).TriageLogAsync(
            path: null, text: "source.cs(1,1): error CS1002: ; expected", question: "Only the final test row.",
            model: null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("UNACCEPTED", result.Answer, StringComparison.Ordinal);
        Assert.NotNull(result.DiagnosticVerification);
        Assert.Equal("failed", result.DiagnosticVerification.Status);
    }

    [Fact]
    public async Task Inventory_repair_preserves_the_separate_requested_answer()
    {
        const string inventory = "{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1},\"buildSummaryErrors\":[]}";
        const string requested = "Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8";
        var invalidInventory = inventory[..^1] + ",\"extra\":true}";
        var envelope = "{\"inventory\":" + invalidInventory + ",\"requestedAnswer\":" + JsonSerializer.Serialize(requested) + "}";
        var client = new FakeLocalModelClient
        {
            RoutedAnswerFactory = (call, _) => call.Prompt.StartsWith("Return exactly the required JSON inventory", StringComparison.Ordinal)
                ? inventory : envelope,
        };
        var result = await new LocalTasks(client).TriageLogAsync(path: null,
            text: "source.cs(1,1): error CS1002: ; expected\n" + requested,
            question: "Only the final xUnit summary.", model: null, TestContext.Current.CancellationToken);
        Assert.Contains(requested, result.Answer, StringComparison.Ordinal);
        Assert.Equal("passed", result.DiagnosticVerification!.Status);
        Assert.Equal(1, result.DiagnosticVerification.RepairAttempts);
        Assert.Contains("response_schema_mismatch", result.DiagnosticVerification.InitialReasons);
    }

    [Fact]
    public async Task A_fenced_focused_envelope_preserves_normalization_provenance()
    {
        const string inventory = "{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1},\"buildSummaryErrors\":[]}";
        var answer = "```json\n{\"inventory\":" + inventory + ",\"requestedAnswer\":\"Exact requested row\"}\n```";
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = answer }).TriageLogAsync(
            path: null, text: "source.cs(1,1): error CS1002: ; expected", question: "Only the final test row.",
            model: null, TestContext.Current.CancellationToken);
        Assert.Contains("Exact requested row", result.Answer, StringComparison.Ordinal);
        Assert.Equal("passed", result.DiagnosticVerification!.Status);
        Assert.True(result.DiagnosticVerification.FormatNormalized);
        Assert.Equal(0, result.DiagnosticVerification.RepairAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public async Task A_custom_log_question_survives_compiler_evidence_and_keeps_verified_inventory(int paddingLines)
    {
        const string question = "Only the exact final xUnit summary row and skipped test name/reason.";
        const string summary = "Passed! - Failed: 0, Passed: 7, Skipped: 1, Total: 8\n[SKIP] OptionalFixture: feature unavailable";
        const string inventory = "{\"diagnosticLineCount\":1,\"codeCounts\":{\"CS1002\":1},\"buildSummaryErrors\":[]}";
        var envelope = "{\"inventory\":" + inventory + ",\"requestedAnswer\":" + JsonSerializer.Serialize(summary) + "}";
        var client = new FakeLocalModelClient
        {
            MaximumPreflightContextTokens = 2048,
            RoutedAnswerFactory = (call, _) => call.Prompt.StartsWith("Return exactly the required JSON inventory", StringComparison.Ordinal)
                ? inventory : call.Prompt.Contains("CS1002", StringComparison.Ordinal) ? envelope : summary,
        };
        var result = await new LocalTasks(client).TriageLogAsync(path: null,
            text: "source.cs(1,1): error CS1002: ; expected\n" +
                  string.Concat(Enumerable.Repeat("Unrelated setup output.\n", paddingLines)) + summary,
            question: question, model: null, TestContext.Current.CancellationToken);

        Assert.Contains(summary, result.Answer, StringComparison.Ordinal);
        Assert.NotNull(result.CompilerDiagnostics);
        Assert.Equal(1, result.CompilerDiagnostics.DiagnosticLineCount);
        Assert.NotNull(result.DiagnosticVerification);
        Assert.Equal("passed", result.DiagnosticVerification.Status);
        Assert.All(client.Calls.Where(call => !call.Prompt.StartsWith("Return exactly the required JSON inventory", StringComparison.Ordinal)),
            call => Assert.Contains(question, call.Prompt, StringComparison.Ordinal));
        if (paddingLines > 0)
            Assert.Contains(client.Calls, call => !call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Default_log_triage_retains_an_explicit_whole_input_comparison()
    {
        const string source = "Restore completed.\nPassed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8";
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "triage_log");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        var result = await new LocalTasks(new FakeLocalModelClient { Answer = "All tests passed." })
            .TriageLogAsync(path: null, text: source, question: null, model: null,
                TestContext.Current.CancellationToken);
        var report = CloudTokenMethodology.Apply(call.Complete(), call.Comparison.Scenario("request"),
            result.Answer, call.Comparison.Profile);
        Assert.Equal(O200kCloudTokenProfile.Instance.CountText(source),
            report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
    }

    [Theory]
    [InlineData(0, CloudContextPresence.NotPresent)]
    [InlineData(2000, CloudContextPresence.NotPresent)]
    [InlineData(2000, CloudContextPresence.AlreadyPresent)]
    [InlineData(2000, CloudContextPresence.Unknown)]
    public async Task A_log_question_does_not_credit_irrelevant_padding_as_cloud_context(
        int paddingLines, CloudContextPresence presence)
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-log-question-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string question = "Only the exact final xUnit summary row and skipped test name/reason. No setup summary.";
            const string summary = "Passed! - Failed: 0, Passed: 7, Skipped: 1, Total: 8\n[SKIP] OptionalFixture: feature unavailable";
            var path = Path.Combine(root, "test.log");
            await File.WriteAllTextAsync(path,
                string.Concat(Enumerable.Repeat("Restoring an unrelated setup package.\n", paddingLines)) + summary,
                TestContext.Current.CancellationToken);
            var client = new FakeLocalModelClient { Answer = summary, MaximumPreflightContextTokens = 2048 };
            var tasks = new LocalTasks(client);
            var response = await new UsageReporting(root).InvokeAsync("locallm", "triage_log", Guid.NewGuid(),
                async token => new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = await LocalLm.Mcp.LocalLmTools.TriageLog(tasks, path: path, question: question,
                            cancellationToken: token, sourceContext: presence,
                            contextBasis: CloudContextBasis.ExplicitTaskInput),
                    }],
                }, TestContext.Current.CancellationToken,
                requestJson: JsonSerializer.Serialize(new { path, question, sourceContext = presence,
                    contextBasis = CloudContextBasis.ExplicitTaskInput }));

            var report = UsageReportJson.Read(response.Meta!["localai/usage"]!.ToJsonString());
            Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Status);
            Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
            Assert.Contains(":" + presence + ":", report.Comparison.ScenarioId!);
            Assert.NotEmpty(client.Calls);
            Assert.All(client.Calls, call => Assert.Contains(question, call.Prompt, StringComparison.Ordinal));
            if (paddingLines > 0)
            {
                Assert.True(client.Calls.Count(call => call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal)) > 1);
                Assert.Contains(client.Calls, call => !call.Prompt.Contains("BEGIN LOG FRAGMENT", StringComparison.Ordinal));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
