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
                    var routing = job.Receipt?.Routing;
                    call.ModelCalled(attempt.Model,
                        routing is not null && routing.SelectedModel == attempt.Model
                            ? Offload(routing.ResidencyShortfall) : UsageOffload.Unknown,
                        inputTokens: attempt.BackendInputTokens, outputTokens: attempt.BackendOutputTokens,
                        attemptId: attempt.AttemptId);
                    call.AddTiming(UsageTiming.LoadMilliseconds, (long)attempt.LoadDuration.TotalMilliseconds);
                }
                if (job.Receipt is { } receipt)
                {
                    if (attempts.Length == 0)
                        call.ModelCalled(receipt.Model, receipt.Routing is { } routing
                            ? Offload(routing.ResidencyShortfall) : UsageOffload.Unknown);
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

    private static UsageOffload Offload(ResidencyShortfall shortfall) => shortfall switch
    {
        ResidencyShortfall.PartialOffload => UsageOffload.Partial,
        ResidencyShortfall.Cpu => UsageOffload.Cpu,
        _ => UsageOffload.None,
    };
}
