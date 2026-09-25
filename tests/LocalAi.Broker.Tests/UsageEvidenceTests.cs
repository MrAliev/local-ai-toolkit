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

    [Fact]
    public async Task A_job_that_ran_without_a_residency_check_says_so()
    {
        var model = await SingleModelAsync(null, Receipt(Guid.Empty, "qwen3-embedding:8b-q8_0", 1, 2));

        Assert.Equal(new UsageModelUse("qwen3-embedding:8b-q8_0", 1, UsageOffload.Unknown, "no_residency_check"), model);
    }

    [Fact]
    public async Task A_routed_receipt_without_a_residency_proof_is_not_reported_as_fully_resident()
    {
        var model = await SingleModelAsync(null,
            Receipt(Guid.Empty, "glm-ocr:bf16", 1, 2) with { Routing = Routing("glm-ocr:bf16", ResidencyShortfall.None, null) });

        Assert.Equal(new UsageModelUse("glm-ocr:bf16", 1, UsageOffload.Unknown, "not_recorded"), model);
    }

    [Fact]
    public async Task A_legacy_attempt_is_not_reported_as_fully_resident_by_a_receipt_without_proof()
    {
        var model = await SingleModelAsync("qwen3-vl:8b-instruct-q8_0",
            Receipt(Guid.Empty, "qwen3-vl:8b-instruct-q8_0", 1, 2) with
            {
                Routing = Routing("qwen3-vl:8b-instruct-q8_0", ResidencyShortfall.None, null),
            });

        Assert.Equal(new UsageModelUse("qwen3-vl:8b-instruct-q8_0", 1, UsageOffload.Unknown, "not_recorded"), model);
    }

    [Fact]
    public async Task A_legacy_attempt_of_the_final_model_keeps_the_proof_its_own_receipt_carries()
    {
        var model = await SingleModelAsync("qwen3-vl:8b-instruct-q8_0",
            Receipt(Guid.Empty, "qwen3-vl:8b-instruct-q8_0", 1, 2) with
            {
                Routing = Routing("qwen3-vl:8b-instruct-q8_0", ResidencyShortfall.PartialOffload, 42),
            });

        Assert.Equal(new UsageModelUse("qwen3-vl:8b-instruct-q8_0", 1, UsageOffload.Partial, null), model);
    }

    [Fact]
    public async Task A_legacy_attempt_does_not_take_a_proof_made_at_another_context()
    {
        var model = await SingleModelAsync("qwen3-vl:8b-instruct-q8_0",
            Receipt(Guid.Empty, "qwen3-vl:8b-instruct-q8_0", 1, 2) with
            {
                Routing = Routing("qwen3-vl:8b-instruct-q8_0", ResidencyShortfall.PartialOffload, 42) with { ContextTokens = 8192 },
            });

        Assert.Equal(new UsageModelUse("qwen3-vl:8b-instruct-q8_0", 1, UsageOffload.Unknown, "not_recorded"), model);
    }

    [Fact]
    public async Task Each_attempt_keeps_the_residency_of_its_own_load()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "read_image");
        var job = Guid.NewGuid();
        var store = new ModelTelemetryStore(root);
        await store.AppendAsync(Record(job, "glm-ocr:bf16") with
        {
            Residency = ResidencyShortfall.PartialOffload, VramResidentPercent = 60, ResidencyVerifiedAtUtc = DateTimeOffset.UnixEpoch,
        }, TestContext.Current.CancellationToken);
        await store.AppendAsync(Record(job, "qwen3-vl:8b-instruct-q8_0") with
        {
            Residency = ResidencyShortfall.None, VramResidentPercent = 100, ResidencyVerifiedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
        }, TestContext.Current.CancellationToken);
        call.LinkJob(job);
        call.RegisterJobEvidence(root, job, Receipt(job, "qwen3-vl:8b-instruct-q8_0", 1, 2) with
        {
            Routing = Routing("qwen3-vl:8b-instruct-q8_0", ResidencyShortfall.None, 100),
        });

        await UsageEvidence.CollectAsync(call);

        Assert.Equal(
            [
                new UsageModelUse("glm-ocr:bf16", 1, UsageOffload.Partial, null),
                new UsageModelUse("qwen3-vl:8b-instruct-q8_0", 1, UsageOffload.None, null),
            ],
            call.Complete().Models.Actual.OrderBy(model => model.Model, StringComparer.Ordinal));
    }

    private async Task<UsageModelUse> SingleModelAsync(string? recordedModel, LocalUsageReceipt receipt)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "read_image");
        var job = Guid.NewGuid();
        if (recordedModel is not null)
            await new ModelTelemetryStore(root).AppendAsync(Record(job, recordedModel), TestContext.Current.CancellationToken);
        call.LinkJob(job);
        call.RegisterJobEvidence(root, job, receipt with { JobId = job });
        await UsageEvidence.CollectAsync(call);
        return Assert.Single(call.Complete().Models.Actual);
    }

    private static LocalRoutingReceipt Routing(string model, ResidencyShortfall shortfall, int? residentPercent) =>
        new(LocalTaskProfile.Ocr, model, 2048, true, false, "none:pass", 0, 0, 0,
            ResidencyShortfall: shortfall, VramResidentPercent: residentPercent);

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
