namespace LocalAi.Broker;

public enum BackendLiveness
{
    Healthy,
    Unhealthy,
    Inconclusive
}

public sealed record BackendProbeResult(
    BackendLiveness Liveness,
    string Code)
{
    public static BackendProbeResult Healthy(string code = "healthy") =>
        new(BackendLiveness.Healthy, code);

    public static BackendProbeResult Unhealthy(string code) =>
        new(BackendLiveness.Unhealthy, code);

    public static BackendProbeResult Inconclusive(string code) =>
        new(BackendLiveness.Inconclusive, code);
}

public sealed record BackendWatchdogPolicy(
    TimeSpan SilenceBeforeProbe,
    TimeSpan ProbeInterval,
    TimeSpan ProbeTimeout,
    int RequiredUnhealthyProbes)
{
    public static BackendWatchdogPolicy Default { get; } = new(
        SilenceBeforeProbe: TimeSpan.FromMinutes(10),
        ProbeInterval: TimeSpan.FromMinutes(1),
        ProbeTimeout: TimeSpan.FromSeconds(10),
        RequiredUnhealthyProbes: 2);

    public void Validate()
    {
        if (SilenceBeforeProbe <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SilenceBeforeProbe));
        }

        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
        }

        if (ProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        }

        if (RequiredUnhealthyProbes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequiredUnhealthyProbes));
        }
    }
}

/// <summary>
/// The backend is not there. The watchdog reaches this conclusion after its liveness probes;
/// transport failures with a more precise stable meaning derive from this type so the host still
/// gives them the same retry residency while preserving their own failure code for a caller.
/// </summary>
public class BackendUnavailableException : Exception
{
    public BackendUnavailableException(string code)
        : base($"The model backend is unavailable: {code}.") => Code = code;

    /// <summary>
    /// With what the backend itself said. Kept because losing it is the defect #349 was filed
    /// for: the one sentence that explains the failure existed for a moment at every layer and
    /// was dropped by each of them.
    /// </summary>
    public BackendUnavailableException(string code, string detail)
        : base($"The model backend is unavailable: {code}. {detail}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// Ollama answered, but its own HTTP hop to llama-server was refused. A distinct stable failure
/// code lets the indexer reduce per-request fan-out without parsing Ollama's quoted response.
/// </summary>
public sealed class BackendRunnerUnavailableException(string detail)
    : BackendUnavailableException("runner_unreachable", detail);
