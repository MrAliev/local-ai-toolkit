using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// The heartbeat is how every client tells a live broker from a dead one, and this loop is also
/// the only reader of the shutdown request. Both were riding on a bare <c>store.Publish</c>: the
/// first <see cref="IOException"/> — an antivirus holding <c>host.json</c> for a moment, the
/// ordinary contention the store's own retry budget exists for — ended the loop for the life of
/// the process.
///
/// What follows from that is worse than a missing heartbeat. Clients see one older than five
/// seconds, decide the broker is dead and start a replacement, which finds <c>broker.lock</c>
/// still held and exits 0; and the broker cannot be asked to stop either, because nothing else
/// ever reads the shutdown request. A broker that is still working its queue perfectly becomes
/// permanently unreachable and unstoppable, from one moment of disk contention.
///
/// Neither test waits out a duration. Each races the condition it wants against the loop's own
/// task, so an unfixed loop fails the test the instant it dies, and a correct one finishes as
/// soon as the condition holds.
/// </summary>
public sealed class AFailedHeartbeatDoesNotKillTheBrokerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-heartbeat-" + Guid.NewGuid().ToString("N"));

    private readonly CancellationTokenSource _stop = new();

    public AFailedHeartbeatDoesNotKillTheBrokerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_write_that_fails_does_not_end_the_loop()
    {
        var attempts = 0;
        var publishedAgain = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(
            publish: _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new IOException("host.json is held by another process.");
                }

                publishedAgain.TrySetResult();
            },
            drainRequested: () => false,
            requestDrain: () => { });

        await FirstOf(publishedAgain.Task, loop);

        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task A_broker_whose_heartbeat_cannot_be_written_can_still_be_stopped()
    {
        var owner = Owner();
        BrokerShutdownRequestStore.Write(
            _root,
            new BrokerShutdownRequest(owner.ProcessId, owner.StartedAtUtc));
        var drained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = false;
        var loop = Loop(
            publish: _ => throw new IOException("host.json is held by another process."),
            drainRequested: () => requested,
            requestDrain: () =>
            {
                requested = true;
                drained.TrySetResult();
            },
            owner: owner);

        await FirstOf(drained.Task, loop);

        Assert.True(requested);
        Assert.False(File.Exists(BrokerShutdownRequestStore.PathFor(_root)));
    }

    /// <summary>
    /// Surviving the failure must not mean hiding it. A broker whose heartbeat has been failing
    /// for an hour looks exactly like one that is fine, right up to the moment somebody asks why
    /// clients keep starting replacements — and #338 put the broker's own failures on disk
    /// precisely so that question has an answer after the fact.
    /// </summary>
    [Fact]
    public async Task A_write_that_fails_is_written_down()
    {
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(
            publish: _ => throw new UnauthorizedAccessException("host.json is not writable."),
            drainRequested: () => false,
            requestDrain: () => { },
            publishFailed: exception => reported.TrySetResult(exception));

        await FirstOf(reported.Task, loop);

        Assert.IsType<UnauthorizedAccessException>(await reported.Task);
    }

    /// <summary>
    /// Returns when the condition holds, and rethrows if the loop died first — so a loop that
    /// stops on the first failed write reports that failure rather than hanging until the test
    /// harness gives up on it.
    /// </summary>
    private static async Task FirstOf(Task condition, Task loop)
    {
        await Task.WhenAny(condition, loop);
        if (loop.IsCompleted)
        {
            await loop;
        }

        await condition;
    }

    private Task Loop(
        Action<BrokerProcessState> publish,
        Func<bool> drainRequested,
        Action requestDrain,
        BrokerProcessState? owner = null,
        Action<Exception>? publishFailed = null) =>
        new HeartbeatLoop(
            owner ?? Owner(),
            _root,
            publish,
            () => new BrokerBackendState(true, "http://127.0.0.1:11434/"),
            drainRequested,
            requestDrain,
            publishFailed ?? (_ => { }),
            TimeSpan.FromMilliseconds(1)).RunAsync(_stop.Token);

    private static BrokerProcessState Owner() =>
        new(
            42,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            BrokerCompatibilityContract.HostStateSchemaVersion,
            Path.GetFullPath("LocalAi.Broker.dll"),
            BrokerCompatibilityContract.Current);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
