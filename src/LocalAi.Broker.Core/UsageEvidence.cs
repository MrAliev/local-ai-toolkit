using LocalAi.Contracts;

namespace LocalAi.Broker;

public static class UsageEvidence
{
    public static async Task CollectAsync(UsageCall call)
    {
        // Read each runtime once per surface call, rather than once for every translated fragment.
        // Only IDs and receipts enter this collector; request and response contents never do.
        foreach (var group in call.TakeJobEvidence().GroupBy(job => job.RuntimeRoot, StringComparer.OrdinalIgnoreCase))
        {
            ModelTelemetryReadResult read;
            try { read = await new ModelTelemetryStore(group.Key).ReadForReportAsync(CancellationToken.None); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { read = new([], 1); }
            var byJob = read.Records.Where(record => record.SchemaVersion == 2).ToLookup(record => record.JobId);
            foreach (var job in group)
            {
                var attempts = byJob[job.JobId].ToArray();
                foreach (var attempt in attempts)
                {
                    // An attempt's own proof first; a record from an older broker has none, and only
                    // a receipt proven for that same model and context may stand in for it.
                    var (offload, reason) = !MatchesRequest(job, attempt.Model, attempt.ContextTokens)
                        ? (UsageOffload.Unknown, "request_context_mismatch")
                        : attempt.Residency is { } residency
                        ? (Offload(residency), null)
                        : job.Receipt?.Routing is { VramResidentPercent: not null } routing
                            && routing.SelectedModel == job.Receipt.Model
                            && routing.SelectedModel == attempt.Model && routing.ContextTokens == attempt.ContextTokens
                            ? (Offload(routing.ResidencyShortfall), null)
                            : (UsageOffload.Unknown, "not_recorded");
                    call.ModelCalled(attempt.Model, offload, reason,
                        inputTokens: attempt.BackendInputTokens, outputTokens: attempt.BackendOutputTokens,
                        attemptId: attempt.AttemptId);
                    call.AddTiming(UsageTiming.LoadMilliseconds, (long)attempt.LoadDuration.TotalMilliseconds);
                }
                if (job.Receipt is { } receipt)
                {
                    if (attempts.Length == 0 && receipt.InferenceExecuted is false)
                    {
                        if (job.ExpectedModel is null || job.ExpectedModel == receipt.Model)
                        {
                            call.NoInference(job.JobId);
                            call.CacheHit("query_embedding");
                        }
                    }
                    else if (attempts.Length == 0)
                    {
                        var (offload, reason) = receipt.Routing switch
                        {
                            null => (UsageOffload.Unknown, "no_residency_check"),
                            var routing when routing.SelectedModel != receipt.Model ||
                                !MatchesRequest(job, routing.SelectedModel, routing.ContextTokens) =>
                                (UsageOffload.Unknown, "request_context_mismatch"),
                            { VramResidentPercent: null } => (UsageOffload.Unknown, "not_recorded"),
                            var routing => (Offload(routing.ResidencyShortfall), (string?)null),
                        };
                        call.ModelCalled(receipt.Model, offload, reason);
                    }
                    call.AddTiming(UsageTiming.QueueMilliseconds, (long)receipt.QueueDuration.TotalMilliseconds);
                    call.AddTiming(UsageTiming.ExecutionMilliseconds, (long)receipt.ExecutionDuration.TotalMilliseconds);
                }
                else if (attempts.Length > 0)
                {
                    call.AddTiming(UsageTiming.QueueMilliseconds, (long)attempts.Min(record => record.QueueDuration).TotalMilliseconds);
                    foreach (var attempt in attempts)
                        call.AddTiming(UsageTiming.ExecutionMilliseconds, (long)attempt.ExecutionDuration.TotalMilliseconds);
                }
            }
        }
    }

    private static bool MatchesRequest(UsageJobEvidence job, string model, int? contextTokens) =>
        (job.ExpectedModel is null || job.ExpectedModel == model) &&
        (job.ExpectedContextTokens is null || job.ExpectedContextTokens == contextTokens);

    private static UsageOffload Offload(ResidencyShortfall shortfall) => shortfall switch
    {
        ResidencyShortfall.PartialOffload => UsageOffload.Partial,
        ResidencyShortfall.Cpu => UsageOffload.Cpu,
        _ => UsageOffload.None,
    };
}
