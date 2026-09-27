using System.Text;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Contracts.Security;
using LocalAi.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace LocalLm.Tests;

public sealed class McpUsageReportingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-mcp-usage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_sdk_cancellation_notification_cancels_the_call_and_keeps_a_partial_report()
    {
        var incoming = new Pipe(); var outgoing = new Pipe();
        var state = new BlockingState();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithUsageReporting("locallm", root)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, token) =>
            {
                state.RequestId.TrySetResult(context.JsonRpcRequest.Id);
                return next(context, token);
            }))
            .WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream()).WithTools<BlockingTool>();
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var id = Guid.NewGuid();
            var pending = client.CallToolAsync(new CallToolRequestParams
            { Name = "ask_local", Meta = new JsonObject { ["localai/callId"] = id.ToString() } }, cancellation.Token);
            await state.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = await state.RequestId.Task, Reason = "test cancellation" },
                McpJsonUtilities.DefaultOptions, TestContext.Current.CancellationToken);
            // Wait for the observable durable result, not an assumption about machine speed.
            var store = new UsageReportStore(root);
            UsageReport? report;
            while ((report = await store.ReadAsync(id, TestContext.Current.CancellationToken)) is null)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(UsageTechnicalOutcome.Cancelled, report.Outcome.Technical);
            Assert.Equal(UsageValue.Measured(7), report.Volumes[UsageVolume.BackendEvalTokens]);
            Assert.Equal(UsageDelivery.NotAttempted, report.Outcome.Delivery);
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    public sealed class BlockingState
    {
        public TaskCompletionSource<RequestId> RequestId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class BlockingTool(BlockingState state)
    {
        [McpServerTool(Name = "ask_local")]
        public async Task<string> Ask(CancellationToken cancellationToken)
        {
            UsageCall.Current?.ModelCalled("observed-model", UsageOffload.Cpu, inputTokens: 11, outputTokens: 7);
            state.Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "unreachable";
        }
    }

    public sealed class BindingTool
    {
        [McpServerTool(Name = "read_image")]
        public string Read(string[] paths) => throw new InvalidOperationException("Binding must refuse before invocation.");
    }

    [Fact]
    public async Task An_unwritable_report_store_preserves_the_answer_and_marks_the_metadata()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "telemetry"), "blocked", TestContext.Current.CancellationToken);
        var result = await new UsageReporting(root).InvokeAsync("locallm", "ask_local", Guid.NewGuid(),
            _ => ValueTask.FromResult(Text("answer")), TestContext.Current.CancellationToken);
        Assert.Equal("answer", Assert.IsType<TextContentBlock>(result.Content[^1]).Text);
        Assert.NotNull(result.Meta?["localai/usagePersistence"]);
        var report = UsageReportJson.Read(result.Meta!["localai/usage"]!.ToJsonString());
        Assert.Equal(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions)), report.Volumes[UsageVolume.ResponseBytes].Value);
    }

    [Fact]
    public async Task The_registered_sdk_pipeline_covers_binding_errors_before_invocation()
    {
        var incoming = new Pipe(); var outgoing = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMcpServer().WithUsageReporting("locallm", root)
            .WithStreamServerTransport(incoming.Reader.AsStream(), outgoing.Writer.AsStream())
            .WithTools<BindingTool>();
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(incoming.Writer.AsStream(), outgoing.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            var id = Guid.NewGuid();
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "read_image", Arguments = new Dictionary<string, JsonElement>
                { ["paths"] = JsonSerializer.SerializeToElement(42) },
                Meta = new JsonObject { ["localai/callId"] = id.ToString() },
            }, TestContext.Current.CancellationToken);
            Assert.True(result.IsError);
            Assert.NotNull(result.Meta?["localai/usage"]);
            Assert.Equal(UsageTechnicalOutcome.Refused, (await Report(id)).Outcome.Technical);
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task A_readable_input_refusal_is_not_recorded_as_success()
    {
        var id = Guid.NewGuid();
        await new UsageReporting(root).InvokeAsync("locallm", "translate_local", id,
            async token => Text(await LocalLm.Mcp.LocalLmTools.TranslateLocal(null!, "English", "Russian", cancellationToken: token)),
            TestContext.Current.CancellationToken);
        Assert.Equal(UsageTechnicalOutcome.Refused, (await Report(id)).Outcome.Technical);
    }

    [Fact]
    public async Task Cancellation_propagates_and_partial_metrics_remain_retrievable_by_call_id()
    {
        var id = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new UsageReporting(root).InvokeAsync("locallm", "translate_local", id, token =>
            {
                UsageCall.Current?.ModelCalled("qwen3.5:9b", UsageOffload.Partial, inputTokens: 12, outputTokens: 7);
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Text("unreachable"));
            }, cancelled.Token));
        var report = await Report(id);
        Assert.Equal(UsageTechnicalOutcome.Cancelled, report.Outcome.Technical);
        Assert.Equal(CloudTokenMethodology.Version, report.MethodologyVersion);
        Assert.Equal(UsageValue.Measured(7), report.Volumes[UsageVolume.BackendEvalTokens]);
        Assert.Equal(UsageValueStatus.Unknown, report.Volumes[UsageVolume.ResponseBytes].Status);
        Assert.Single((await new UsageReportStore(root).ReadForReportAsync(TestContext.Current.CancellationToken)).Reports);
    }

    [Fact]
    public async Task An_argument_error_before_the_tool_body_still_has_a_report()
    {
        var id = Guid.NewGuid();
        var result = await new UsageReporting(root).InvokeAsync("locallm", "read_image", id,
            _ => throw new ArgumentException("invalid mode"), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.NotNull(result.Meta?["localai/usage"]);
        var report = await Report(id);
        Assert.Equal(UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(UsageModelState.NotCalled, report.Models.State);
    }

    [Fact]
    public async Task Unexpected_errors_return_the_canonical_report_without_exception_contents()
    {
        var id = Guid.NewGuid();
        var result = await new UsageReporting(root).InvokeAsync("locallm", "ask_local", id,
            _ => throw new InvalidOperationException("SECRET-PROMPT"), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.NotNull(result.Meta?["localai/usage"]);
        Assert.DoesNotContain("SECRET-PROMPT", JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions), StringComparison.Ordinal);
        Assert.Equal(UsageTechnicalOutcome.Failed, (await Report(id)).Outcome.Technical);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(99)]
    [InlineData(999)]
    [InlineData(9999)]
    public async Task Json_text_stays_parseable_and_full_serialized_result_includes_its_own_report(int length)
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { name = new string('я', length) });
        var result = await new UsageReporting(root).InvokeAsync("locallm", "local_models_status", id,
            _ => ValueTask.FromResult(Text(json)), TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Content.Count);
        Assert.Equal(json, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        using var parsed = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal(length, parsed.RootElement.GetProperty("name").GetString()!.Length);
        var report = await Report(id);
        Assert.Equal(LocalAi.Contracts.Localization.UsageNotice.Render(report), Assert.IsType<TextContentBlock>(result.Content[1]).Text);
        Assert.NotNull(result.Meta);
        Assert.Equal(Assert.IsType<TextContentBlock>(result.Content[1]).Text, result.Meta["localai/usageNotice"]!.GetValue<string>());
        Assert.Equal(UsageReportJson.Write(report), result.Meta["localai/usage"]!.ToJsonString());
        var serialized = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        Assert.Equal(serialized.Length, report.Volumes[UsageVolume.ResponseCharacters].Value);
        Assert.Equal(Encoding.UTF8.GetByteCount(serialized), report.Volumes[UsageVolume.ResponseBytes].Value);
        Assert.Equal(UsageValueStatus.NotApplicable, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
    }

    [Fact]
    public async Task The_notice_is_outside_the_existing_untrusted_boundary_and_comes_from_the_report()
    {
        var id = Guid.NewGuid();
        var body = UntrustedContent.Wrap("source text", "test");
        var result = await new UsageReporting(root).InvokeAsync("locallm", "ask_local", id,
            _ => ValueTask.FromResult(Text(body)), TestContext.Current.CancellationToken);
        var report = await Report(id);
        var answer = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains(body, answer, StringComparison.Ordinal);
        Assert.StartsWith("Call " + id, answer, StringComparison.Ordinal);
        Assert.Contains("INPUT Unknown (comparison_scenario_unknown)", answer, StringComparison.Ordinal);
        Assert.Equal(UsageDelivery.Delivered, report.Outcome.Delivery);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_tool_call_before_installation_leaves_a_root_the_installer_accepts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installation layout is Windows-only.");
        Directory.CreateDirectory(root);
        var layout = LocalAi.Installer.Core.Activation.InstallationLayout.FromLocalAppData(root);

        await new UsageReporting(layout.Root).InvokeAsync("locallm", "ask_local", Guid.NewGuid(),
            _ => ValueTask.FromResult(Text("answer")), TestContext.Current.CancellationToken);

        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(layout.Root, "telemetry", "usage")));
        using var lease = LocalAi.Installer.Core.Activation.InstallationLayoutLease.Acquire(
            layout, requireFreshInstallerTree: true);
        Assert.Equal(layout.Root, lease.Layout.Root);
    }

    private async Task<UsageReport> Report(Guid id)
    {
        var report = await new UsageReportStore(root).ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(report);
        return report;
    }
    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
