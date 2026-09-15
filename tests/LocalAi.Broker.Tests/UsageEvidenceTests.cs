using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class UsageEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "usage-evidence-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retries_and_partial_jobs_keep_actual_models_counts_and_timings(bool cancelled)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "translate_local");
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var attempt1 = Record(first, "primary"); var attempt2 = Record(first, "fallback");
        var unrelated = Record(Guid.NewGuid(), "unrelated");
        var store = new ModelTelemetryStore(root);
        foreach (var record in new[] { attempt1, attempt2, unrelated }) await store.AppendAsync(record, TestContext.Current.CancellationToken);
        call.LinkJob(first); call.LinkJob(second);
        call.RegisterJobEvidence(root, first, Receipt(first, "fallback", 20, 50));
        call.RegisterJobEvidence(root, second, cancelled ? null : Receipt(second, "embedding", 30, 70));
        if (cancelled) call.Cancelled();
        await UsageEvidence.CollectAsync(call);
        await UsageEvidence.CollectAsync(call);
        var report = call.Complete();
        Assert.Equal(cancelled ? 2 : 3, report.Models.Calls.Value);
        Assert.Equal(2, report.Identity.AttemptIds.Count);
        Assert.DoesNotContain(report.Models.Actual, model => model.Model == "unrelated");
        Assert.Equal(cancelled ? 20 : 50, report.Timings[UsageTiming.QueueMilliseconds].Value);
        Assert.Equal(cancelled ? 50 : 120, report.Timings[UsageTiming.ExecutionMilliseconds].Value);
        Assert.Equal(4, report.Timings[UsageTiming.LoadMilliseconds].Value);
        Assert.Equal(cancelled ? UsageValue.Measured(22) : UsageValue.Unknown("backend_count_not_recorded"), report.Volumes[UsageVolume.BackendPromptTokens]);
        Assert.DoesNotContain(root, UsageReportJson.Write(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_evidence_does_not_mask_cancellation_or_invent_zero_calls()
    {
        var directory = Path.Combine(root, "telemetry", "metrics"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{", TestContext.Current.CancellationToken);
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        var job = Guid.NewGuid(); call.LinkJob(job); call.RegisterJobEvidence(root, job); call.Cancelled();
        await UsageEvidence.CollectAsync(call);
        var report = call.Complete();
        Assert.Equal(UsageTechnicalOutcome.Cancelled, report.Outcome.Technical);
        Assert.Equal(UsageModelState.Unknown, report.Models.State);
    }

    private static LocalUsageReceipt Receipt(Guid job, string model, int queued, int ran) =>
        new(job, "locallm", "chat", model, TimeSpan.FromMilliseconds(queued), TimeSpan.FromMilliseconds(ran), 0, 0, null, null, null);
    private static ModelTelemetryRecord Record(Guid job, string model) => new(job,
        LocalTaskProfile.PlainTranslation, model, 2048, LocalSizeBucket.Small, LocalSizeBucket.Small,
        false, false, false, "structure:pass", ModelExecutionOutcome.Success,
        TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(6),
        0, 0, 0, "1", DateTimeOffset.UtcNow)
        { SchemaVersion = 2, AttemptId = Guid.NewGuid(), BackendInputTokens = 11, BackendOutputTokens = 7 };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
