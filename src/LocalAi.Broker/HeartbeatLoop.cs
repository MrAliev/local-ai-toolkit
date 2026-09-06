using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>
/// Publishes the heartbeat and, on the same tick, notices a shutdown request addressed to this
/// broker. The loop already runs once a second, so noticing costs one file check and no watcher,
/// port or protocol.
///
/// The request is deleted as soon as it is accepted: leaving it behind would shut down the broker
/// that replaces this one, seconds after it starts.
///
/// It is a type rather than a method on the entry point because both of the things it does are
/// load-bearing for a broker nobody is watching — the heartbeat is how clients tell a live broker
/// from a dead one, and this loop is the only reader of the shutdown request — and neither could
/// be tested while it was a private static local to <c>Program</c>.
/// </summary>
public sealed class HeartbeatLoop
{
    private readonly BrokerProcessState _owner;
    private readonly string _runtimeRoot;
    private readonly Action<BrokerProcessState> _publish;
    private readonly Func<BrokerBackendState> _backend;
    private readonly Func<bool> _drainRequested;
    private readonly Action _requestDrain;
    private readonly Action<Exception> _publishFailed;
    private readonly TimeSpan _interval;

    /// <param name="publishFailed">
    /// Required rather than optional. Surviving a failed write is only half of the fix: a broker
    /// whose heartbeat has been failing for an hour looks exactly like a healthy one until
    /// somebody asks why clients keep starting replacements. Every caller has to say where that
    /// goes.
    /// </param>
    public HeartbeatLoop(
        BrokerProcessState owner,
        string runtimeRoot,
        Action<BrokerProcessState> publish,
        Func<BrokerBackendState> backend,
        Func<bool> drainRequested,
        Action requestDrain,
        Action<Exception> publishFailed,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(drainRequested);
        ArgumentNullException.ThrowIfNull(requestDrain);
        ArgumentNullException.ThrowIfNull(publishFailed);
        _owner = owner;
        _runtimeRoot = runtimeRoot;
        _publish = publish;
        _backend = backend;
        _drainRequested = drainRequested;
        _requestDrain = requestDrain;
        _publishFailed = publishFailed;
        _interval = interval ?? DefaultInterval;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_interval, TimeSpan.Zero);
    }

    /// <summary>
    /// How often the heartbeat is written. A client treats a heartbeat older than
    /// <c>MaximumHeartbeatSkew</c>, five seconds, as a dead broker, so this is the rate that
    /// decides how much of that budget a single slow write may spend.
    /// </summary>
    public static TimeSpan DefaultInterval { get; } = TimeSpan.FromSeconds(1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_interval, cancellationToken);
            try
            {
                _publish(_owner with
                {
                    HeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Backend = _backend(),
                });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A heartbeat is an observation, and a missed one costs the next tick. Ending the
                // loop costs the process: clients read a heartbeat older than five seconds as a
                // dead broker and start a replacement, which finds broker.lock still held and
                // exits 0 — and this loop is the only reader of the shutdown request, so the
                // broker cannot be asked to stop either. The store already spends two seconds
                // retrying the rename before it gives up, so what arrives here has outlasted the
                // contention it was written for; trying again in a second is the whole remedy.
                _publishFailed(exception);
            }

            if (_drainRequested())
            {
                continue;
            }

            var request = BrokerShutdownRequestStore.Read(_runtimeRoot);
            if (request is not null &&
                request.ProcessId == _owner.ProcessId &&
                request.StartedAtUtc == _owner.StartedAtUtc)
            {
                BrokerShutdownRequestStore.Delete(_runtimeRoot);
                _requestDrain();
            }
        }
    }
}
