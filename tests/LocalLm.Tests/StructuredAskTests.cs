using System.IO.Pipelines;
using System.Text.Json;
using LocalAi.Contracts;
using LocalLm.Core;
using LocalLm.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalLm.Tests;

public sealed class StructuredAskTests
{
    private const string Schema = """{"schemaVersion":1,"fields":[{"name":"label","type":"String","allowedValues":["bug","question"]}]}""";

    [Theory]
    [InlineData("{\"label\":\"bug\"}", StructuredValidationStatus.Valid)]
    [InlineData("{\"label\":5}", StructuredValidationStatus.SchemaMismatch)]
    [InlineData("{}", StructuredValidationStatus.SchemaMismatch)]
    [InlineData("", StructuredValidationStatus.Empty)]
    [InlineData("Result: {\"label\":\"bug\"}", StructuredValidationStatus.InvalidJson)]
    public async Task Final_response_is_validated_even_when_the_receipt_claims_success(string answer, StructuredValidationStatus status)
    {
        var model = new ResponseClient(answer);
        using var usage = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        var result = await new LocalTasks(model).AskAsync(LocalTaskProfile.Classification, "Classify: app crashes", [], null,
            TestContext.Current.CancellationToken, StructuredOutputSchema.Parse(Schema));
        Assert.Equal(status, result.Validation!.Status);
        Assert.Contains(JsonSerializer.Serialize(StructuredOutputSchema.Parse(Schema)), model.System!, StringComparison.Ordinal);
        Assert.Equal(answer, result.Answer);
        var report = usage.Complete();
        Assert.Equal(status == StructuredValidationStatus.Valid ? UsageValidation.Passed : UsageValidation.Failed, report.Outcome.Validation);
        if (status != StructuredValidationStatus.Valid)
        {
            Assert.Equal(0, result.SavedTokens);
            Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
            Assert.Equal("result_rejected", report.Outcome.TechnicalReason);
        }
    }

    [Fact]
    public async Task Cancellation_after_inference_cannot_deliver_unvalidated_output()
    {
        using var cancelled = new CancellationTokenSource();
        var model = new ResponseClient("{\"label\":\"bug\"}") { AfterInference = cancelled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalTasks(model).AskAsync(
            LocalTaskProfile.Extraction, "Extract", [], null, cancelled.Token, StructuredOutputSchema.Parse(Schema)));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Unsupported_profile_rejects_the_contract_before_inference()
    {
        var model = new ResponseClient("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalTasks(model).AskAsync(
            LocalTaskProfile.ShortSummary, "Extract", [], null, TestContext.Current.CancellationToken, StructuredOutputSchema.Parse(Schema)));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Mcp_binds_the_optional_contract_and_preserves_the_untrusted_boundary()
    {
        var incoming = new Pipe();
        var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new LocalTasks(new ResponseClient("{\"label\":5}")));
        builder.Services.AddMcpServer().WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithToolsFromAssembly(typeof(LocalLmTools).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "ask_local",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Classify this crash"),
                    ["taskProfile"] = JsonSerializer.SerializeToElement("Classification"),
                    ["outputSchema"] = JsonSerializer.SerializeToElement(Schema),
                },
            }, TestContext.Current.CancellationToken);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.Contains("<untrusted-content", text, StringComparison.Ordinal);
            Assert.Contains("SchemaMismatch", text, StringComparison.Ordinal);
            Assert.Contains("\"isValid\":false", text, StringComparison.Ordinal);
            Assert.Contains("wrong_type", text, StringComparison.Ordinal);
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    private sealed class ResponseClient(string answer) : ILocalModelClient
    {
        public string? System { get; private set; }
        public int Calls { get; private set; }
        public Action? AfterInference { get; init; }
        public Task<LocalJobResult<string>> RoutedStructuredChatAsync(LocalTaskProfile profile, string prompt, string system,
            LocalWorkloadMetadata workload, string? modelOverride, int? requestedContextTokens, StructuredOutputSchema outputSchema,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            System = system;
            Calls++;
            AfterInference?.Invoke();
            return Task.FromResult(new LocalJobResult<string>(answer, new LocalUsageReceipt(Guid.NewGuid(), "local-lm", "chat", "test",
                TimeSpan.Zero, TimeSpan.Zero, prompt.Length, 10, null, null, null,
                new LocalRoutingReceipt(profile, "test", 2048, false, true, "none:pass", 0, 0, 0, false))));
        }
        public Task<LocalJobResult<string>> ChatAsync(string model, string prompt, string? system, IReadOnlyList<string>? imagesBase64, LocalJobPriority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<string>> RoutedChatAsync(LocalTaskProfile profile, string prompt, string? system, IReadOnlyList<string>? imagesBase64, LocalWorkloadMetadata workload, LocalWorkflowHint? workflow, string? modelOverride, int? requestedContextTokens, LocalJobPriority priority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<IReadOnlyList<string>>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelsStatusOutput>> GetModelsStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelPreflightOutput>> PreflightModelAsync(string model, int contextTokens, string catalogVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<ModelMaintenanceJobOutput>> PullModelAsync(string model, string catalogVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalExperimentReportOutput>> GetExperimentReportAsync(LocalTaskProfile profile, string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalJobResult<LocalModelFeedbackOutput>> ApplyFeedbackAsync(LocalTaskProfile profile, string model, ExperimentOwnerAction action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
