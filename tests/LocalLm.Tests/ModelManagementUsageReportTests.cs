using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;
using LocalAi.Mcp;
using LocalLm.Core;
using LocalLm.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalLm.Tests;

public sealed class ModelManagementUsageReportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-model-usage-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("local_models_status", "InstalledModels")]
    [InlineData("local_models_sync", "Outcome")]
    [InlineData("local_model_experiment_report", "Attempts")]
    [InlineData("local_model_feedback", "Action")]
    public async Task A_management_answer_keeps_its_json_and_shows_the_report_of_the_call(string tool, string property)
    {
        var (result, report) = await CallAsync(new ScriptedBroker(), tool, Arguments(tool));

        using var document = JsonDocument.Parse(JsonBlock(result));
        Assert.True(document.RootElement.TryGetProperty(property, out _));
        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
        Assert.Equal(UsageNotice.Render(report), result.Meta?["localai/usageNotice"]?.GetValue<string>());
        Assert.Equal(tool, report.Identity.Operation);
        Assert.Equal(UsageTechnicalOutcome.Succeeded, report.Outcome.Technical);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.All(report.CloudTokens.Values, value => Assert.Equal(UsageValue.NotApplicable("administrative_operation"), value));
        Assert.Single((await new UsageReportStore(root).ReadForReportAsync(TestContext.Current.CancellationToken)).Reports);
    }

    [Fact]
    public async Task An_argument_the_tool_cannot_parse_is_refused_and_the_report_is_still_shown()
    {
        var broker = new ScriptedBroker();
        var (result, report) = await CallAsync(broker, "local_model_experiment_report",
            new Dictionary<string, object?> { ["taskProfile"] = "NotAProfile", ["model"] = "qwen3.5:9b" });

        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal("invalid_input", report.Outcome.TechnicalReason);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.Equal(0, broker.Requests);
    }

    [Fact]
    public async Task A_broker_failure_is_a_failed_call_and_the_report_is_still_shown()
    {
        var broker = new ScriptedBroker
        {
            Reply = (request, _) => throw new BrokerJobFailedException(request.JobId, "InvalidOperationException", "catalog unreadable"),
        };
        var (result, report) = await CallAsync(broker, "local_models_status", new Dictionary<string, object?>());

        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
        Assert.Equal(UsageTechnicalOutcome.Failed, report.Outcome.Technical);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
    }

    [Theory]
    [InlineData(1000, UsageOffload.None)]
    [InlineData(400, UsageOffload.Partial)]
    [InlineData(0, UsageOffload.Cpu)]
    public async Task A_preflight_reports_the_model_it_loaded_without_a_task_and_that_loads_residency(long vram, UsageOffload offload)
    {
        var (result, report) = await CallAsync(new ScriptedBroker { PreflightVram = vram }, "local_model_preflight", Preflight(2048));

        Assert.Equal(UsageModelState.Loaded, report.Models.State);
        Assert.Equal(new UsageModelUse("qwen3.5:9b", 1, offload, null), Assert.Single(report.Models.Actual));
        Assert.All(report.CloudTokens.Values, value => Assert.Equal(UsageValue.NotApplicable("administrative_operation"), value));
        using var document = JsonDocument.Parse(JsonBlock(result));
        Assert.Equal("qwen3.5:9b", document.RootElement.GetProperty("Model").GetString());
        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
    }

    [Fact]
    public async Task A_preflight_the_broker_failed_does_not_claim_a_load_it_cannot_prove()
    {
        var broker = new ScriptedBroker
        {
            Reply = (request, _) => throw new BrokerJobFailedException(request.JobId, "ModelPreflightException", "not fully resident"),
        };
        var (result, report) = await CallAsync(broker, "local_model_preflight", Preflight(2048));

        Assert.Equal(UsageModelState.Unknown, report.Models.State);
        Assert.Equal(UsageTechnicalOutcome.Failed, report.Outcome.Technical);
        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
    }

    [Fact]
    public async Task A_preflight_refused_before_reaching_the_broker_loaded_nothing()
    {
        var broker = new ScriptedBroker();
        var (result, report) = await CallAsync(broker, "local_model_preflight", Preflight(0));

        Assert.Equal(0, broker.Requests);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(UsageNotice.Render(report), NoticeBlock(result));
    }

    [Fact]
    public async Task A_cancelled_preflight_keeps_a_partial_report_without_claiming_a_load()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new ScriptedBroker
        {
            Reply = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            },
        };
        var tasks = new ModelManagementTasks(new BrokerLocalModelClient(broker));
        var id = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = new UsageReporting(root).InvokeAsync("locallm", "local_model_preflight", id,
            async token => new CallToolResult
            {
                Content = [new TextContentBlock { Text = await LocalLmTools.LocalModelPreflight(tasks, "qwen3.5:9b", "1", 2048, token) }],
            }, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var report = await Report(id);
        Assert.Equal(UsageTechnicalOutcome.Cancelled, report.Outcome.Technical);
        Assert.Equal(UsageModelState.Unknown, report.Models.State);
    }

    private static Dictionary<string, object?> Preflight(int contextTokens) => new()
    {
        ["model"] = "qwen3.5:9b", ["catalogVersion"] = "1", ["contextTokens"] = contextTokens,
    };

    private static Dictionary<string, object?> Arguments(string tool) => tool switch
    {
        "local_model_experiment_report" => new() { ["taskProfile"] = "ShortSummary", ["model"] = "qwen3.5:9b" },
        "local_model_feedback" => new() { ["taskProfile"] = "ShortSummary", ["model"] = "qwen3.5:9b", ["action"] = "FallbackOnly" },
        _ => new(),
    };

    private static string JsonBlock(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static string NoticeBlock(CallToolResult result)
    {
        Assert.Equal(2, result.Content.Count);
        return Assert.IsType<TextContentBlock>(result.Content[1]).Text;
    }

    private async Task<(CallToolResult Result, UsageReport Report)> CallAsync(
        ScriptedBroker broker, string tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var id = Guid.NewGuid();
        var incoming = new Pipe();
        var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new ModelManagementTasks(new BrokerLocalModelClient(broker)));
        builder.Services.AddMcpServer().WithUsageReporting("locallm", root)
            .WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithToolsFromAssembly(typeof(LocalLmTools).Assembly);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = tool,
                Arguments = arguments.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
                Meta = new JsonObject { ["localai/callId"] = id.ToString() },
            }, TestContext.Current.CancellationToken);
            return (result, await Report(id));
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    private async Task<UsageReport> Report(Guid id)
    {
        var report = await new UsageReportStore(root).ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(report);
        return report;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    internal sealed class ScriptedBroker : IBrokerClient
    {
        private int requests;

        public ScriptedBroker() => Reply = (request, _) => Task.FromResult(Answer(request));

        public int Requests => Volatile.Read(ref requests);

        public long PreflightVram { get; init; } = 1000;

        public Func<LocalJobRequest, CancellationToken, Task<object>> Reply { get; init; }

        public async Task<LocalJobResult<T>> ExecuteAsync<T>(LocalJobRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref requests);
            var value = await Reply(request, cancellationToken);
            return new LocalJobResult<T>((T)value, new LocalUsageReceipt(request.JobId, "local-lm", "model-control", "n/a",
                TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null));
        }

        private object Answer(LocalJobRequest request) => request.Payload switch
        {
            ModelControlJobPayload { Operation: ModelControlOperation.Preflight } preflight =>
                new LocalModelPreflightOutput(preflight.Model!, preflight.ContextTokens!.Value, preflight.CatalogVersion!,
                    1000, PreflightVram, PreflightVram >= 1000, DateTimeOffset.UtcNow),
            ModelControlJobPayload { Operation: ModelControlOperation.Status } =>
                new LocalModelsStatusOutput(["qwen3.5:9b"], [], ["translategemma:12b"], [], "1"),
            ModelControlJobPayload { Operation: ModelControlOperation.ExperimentReport } report =>
                new LocalExperimentReportOutput(report.Profile!.Value, report.Model!, 3, 2, 1, 0,
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1, 2, 500),
            ModelControlJobPayload { Operation: ModelControlOperation.Feedback } feedback =>
                new LocalModelFeedbackOutput(feedback.Profile!.Value, feedback.Model!, feedback.OwnerAction!.Value),
            ModelMaintenanceJobPayload => new ModelMaintenanceJobOutput("success"),
            _ => throw new NotSupportedException(request.Payload.GetType().Name),
        };
    }
}
