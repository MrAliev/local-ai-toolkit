using System.Diagnostics;

namespace LocalAi.Contracts;

/// <summary>Observations owned by one surface invocation, flowing through its asynchronous work.</summary>
public sealed class UsageCall : IDisposable
{
    private static readonly AsyncLocal<UsageCall?> Ambient = new();
    private readonly UsageCall? previous;
    private readonly UsageReport initial;
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly HashSet<Guid> jobs = [];
    private readonly HashSet<Guid> inferenceJobs = [];
    private readonly HashSet<Guid> attempts = [];
    private readonly HashSet<Guid> workflows = [];
    private readonly List<(UsageModelUse Use, long? Input, long? Output)> models = [];
    private readonly List<UsageModelUse> loads = [];
    private readonly Dictionary<UsageVolume, UsageValue> volumes = [];
    private readonly Dictionary<UsageTiming, UsageValue> timings = [];
    private readonly Dictionary<(string Root, Guid Job), UsageJobEvidence> evidence = [];
    private UsageOutcome outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.NotApplicable,
        UsageDelivery.Delivered, null, "no_structural_validator", null);
    private UsageCache cache = UsageCache.NotApplicable;
    private string? cacheReason = "no_result_cache";
    private bool pendingInference;
    private UsageReport? completed;
    public static UsageCall? Current => Ambient.Value;
    public Guid CallId => initial.Identity.CallId;
    public RuntimeCloudComparison Comparison { get; } = new();

    public UsageCall(UsageSurface surface, string component, string operation, Guid? callId = null)
    {
        initial = UsageReportingRegistry.CreateReport(surface, component, operation, callId ?? Guid.NewGuid());
        var tool = UsageReportingRegistry.Find(surface, component, operation).McpTool ?? operation;
        if (tool is "search_code" or "get_code_chunk" or "search_documents")
            Comparison.UnknownBaseline("retrieval_baseline_not_established");
        previous = Ambient.Value;
        Ambient.Value = this;
    }

    public void Refused(string reason) => Change(() => outcome = outcome with
    { Technical = UsageTechnicalOutcome.Refused, TechnicalReason = reason, Delivery = UsageDelivery.NotAttempted, DeliveryReason = reason });
    public void Failed(string reason) => Change(() => outcome = outcome with
    { Technical = UsageTechnicalOutcome.Failed, TechnicalReason = reason, Delivery = UsageDelivery.NotAttempted, DeliveryReason = reason });
    public void Cancelled() => Change(() => outcome = outcome with
    { Technical = UsageTechnicalOutcome.Cancelled, TechnicalReason = "cancelled", Delivery = UsageDelivery.NotAttempted, DeliveryReason = "cancelled" });
    public void Validated(bool passed) => Change(() => outcome = outcome with
    { Validation = passed ? UsageValidation.Passed : UsageValidation.Failed, ValidationReason = passed ? null : "structural_validation_failed" });
    public void DeliveryFailed(string reason) => Change(() => outcome = outcome with
    { Delivery = UsageDelivery.Failed, DeliveryReason = reason });
    public void ExitCode(int exitCode) => Change(() =>
    {
        if (exitCode == 0 || outcome.Technical != UsageTechnicalOutcome.Succeeded || outcome.Delivery == UsageDelivery.Failed) return;
        outcome = outcome with
        {
            Technical = exitCode == 130 ? UsageTechnicalOutcome.Cancelled : exitCode is 1 or 2 or 64 or 69 ? UsageTechnicalOutcome.Refused : UsageTechnicalOutcome.Failed,
            TechnicalReason = "exit_" + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Delivery = UsageDelivery.NotAttempted, DeliveryReason = "command_not_completed",
        };
    });
    public void CacheHit(string reason = "result_cache") => Change(() => { cache = UsageCache.Hit; cacheReason = reason; });
    public void UnknownInference() => Change(() => pendingInference = true);
    public void NoInference() => Change(() => { pendingInference = false; inferenceJobs.Clear(); });
    public void NoInference(Guid jobId) => Change(() => inferenceJobs.Remove(jobId));
    public void LinkJob(Guid jobId, Guid? workflowId = null, bool inference = true) => Change(() =>
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job ID is required.", nameof(jobId));
        jobs.Add(jobId);
        if (workflowId is { } id && id != Guid.Empty) workflows.Add(id);
        if (inference) inferenceJobs.Add(jobId);
    });
    public void ModelCalled(string model, UsageOffload offload, string? reason = null,
        long? inputTokens = null, long? outputTokens = null, Guid? attemptId = null) => Change(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (attemptId is { } id && !attempts.Add(id)) return;
        models.Add((new(model, 1, offload, offload == UsageOffload.Unknown ? reason ?? "not_recorded" : reason),
            inputTokens is >= 0 ? inputTokens : null, outputTokens is >= 0 ? outputTokens : null));
    });
    public void ModelLoaded(string model, UsageOffload offload, string? reason = null) => Change(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        loads.Add(new(model, 1, offload, offload == UsageOffload.Unknown ? reason ?? "not_recorded" : reason));
    });
    public void Observe(UsageVolume volume, long value) => Change(() => volumes[volume] = UsageValue.Measured(value));
    public void Observe(UsageTiming timing, long value) => Change(() => timings[timing] = UsageValue.Measured(value));
    public IReadOnlyList<Guid> JobIds { get { lock (gate) return jobs.ToArray(); } }
    public void RegisterJobEvidence(string runtimeRoot, Guid jobId, LocalUsageReceipt? receipt = null,
        string? expectedModel = null, int? expectedContextTokens = null) =>
        Change(() =>
        {
            if (receipt is not null && receipt.JobId != jobId)
                throw new ArgumentException("Receipt does not belong to the linked job.", nameof(receipt));
            var key = (runtimeRoot, jobId);
            evidence.TryGetValue(key, out var previous);
            if (receipt is not null || previous is null) evidence[key] = new(runtimeRoot, jobId, receipt)
            {
                ExpectedModel = expectedModel ?? previous?.ExpectedModel,
                ExpectedContextTokens = expectedContextTokens ?? previous?.ExpectedContextTokens,
            };
        });
    public IReadOnlyList<UsageJobEvidence> TakeJobEvidence()
    {
        lock (gate) { var snapshot = evidence.Values.ToArray(); evidence.Clear(); return snapshot; }
    }
    public void AddTiming(UsageTiming timing, long value) => Change(() =>
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (timings.TryGetValue(timing, out var previous) && previous.Status == UsageValueStatus.Unknown) return;
        try { timings[timing] = UsageValue.Measured(checked((previous?.Value ?? 0) + value)); }
        catch (OverflowException) { timings[timing] = UsageValue.Unknown("duration_overflow"); }
    });

    public UsageReport Complete()
    {
        lock (gate)
        {
            if (completed is not null) return completed;
            var measuredVolumes = initial.Volumes.ToDictionary();
            foreach (var item in volumes) measuredVolumes[item.Key] = item.Value;
            measuredVolumes[UsageVolume.BackendPromptTokens] = Sum(models.Select(model => model.Input));
            measuredVolumes[UsageVolume.BackendEvalTokens] = Sum(models.Select(model => model.Output));
            var measuredTimings = initial.Timings.ToDictionary();
            foreach (var item in timings) measuredTimings[item.Key] = item.Value;
            measuredTimings[UsageTiming.ElapsedMilliseconds] = UsageValue.Measured((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var actual = Group(models.Select(model => model.Use));
            var loaded = Group(loads);
            var observedModels = actual.Length > 0
                ? new UsageModels(UsageModelState.Called, UsageValue.Measured(models.Count), actual, cache, null)
                : loaded.Length > 0 ? new UsageModels(UsageModelState.Loaded, UsageValue.Measured(loads.Count), loaded, cache, null)
                : pendingInference || inferenceJobs.Count > 0 ? UsageModels.Unrecorded() : UsageModels.NoCall();
            observedModels = observedModels with
            { Cache = cache, CacheReason = cacheReason };
            return completed = initial with
            {
                Identity = initial.Identity with { JobIds = jobs.ToArray(), AttemptIds = attempts.ToArray(), WorkflowId = workflows.Count == 1 ? workflows.Single() : null },
                Outcome = outcome, Models = observedModels, Volumes = measuredVolumes, Timings = measuredTimings,
            };
        }
    }

    private static UsageModelUse[] Group(IEnumerable<UsageModelUse> uses) =>
        uses.GroupBy(use => (use.Model, use.Offload, use.OffloadReason))
            .Select(group => new UsageModelUse(group.Key.Model, group.LongCount(), group.Key.Offload, group.Key.OffloadReason)).ToArray();

    private static UsageValue Sum(IEnumerable<long?> values)
    {
        var observed = values.ToArray();
        if (observed.Length == 0 || observed.Any(value => value is null)) return UsageValue.Unknown("backend_count_not_recorded");
        try { return UsageValue.Measured(observed.Sum(value => value!.Value)); }
        catch (OverflowException) { return UsageValue.Unknown("backend_count_overflow"); }
    }
    private void Change(Action action)
    {
        lock (gate)
        {
            if (completed is not null) throw new InvalidOperationException("The usage call is already complete.");
            action();
        }
    }
    public void Dispose() => Ambient.Value = previous;
}

public sealed record UsageJobEvidence(string RuntimeRoot, Guid JobId, LocalUsageReceipt? Receipt)
{
    public string? ExpectedModel { get; init; }
    public int? ExpectedContextTokens { get; init; }
}
