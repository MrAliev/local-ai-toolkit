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
/// The backend is not there. Two things reach this conclusion and both say it this way, because
/// what a caller acts on is the name: BrokerHost records `exception.GetType().Name` as the
/// failure code, and BrokerJobFailedException says of that code that it is the stable thing to
/// match on, as against the reason, which is quoted rather than parsed.
///
/// The message no longer names a liveness probe. The watchdog is one producer; the transport is
/// the other, and it reaches the same conclusion by exhausting its retries against a runner that
/// never answered - no probe involved.
/// </summary>
public sealed class BackendUnavailableException : Exception
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
