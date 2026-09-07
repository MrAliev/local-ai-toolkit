using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Three numbers in three assemblies decide whether a working broker is mistaken for a dead one,
/// and none of them mentioned the others: the loop writes a heartbeat every second, the store
/// spends up to two seconds retrying a rename a reader is denying, and a client treats a heartbeat
/// older than five seconds as a dead broker (#396).
///
/// What the arithmetic buys is exactly one failed publish between heartbeats — one second of
/// waiting, two of retrying, one more of waiting, and the write lands four seconds after the last
/// one. **Two consecutive failures do not fit**: seven seconds against a five-second skew, and for
/// that window a healthy broker reads as dead. The consequence is no longer the outage the issue
/// described — `EnsureRunningAsync` re-reads the state every 50 ms while it waits, so the next
/// successful heartbeat ends it — but it still costs a process launch nobody needed.
///
/// This pins the guarantee rather than the limit: widening the margin should not fail a test.
/// </summary>
public sealed class TheHeartbeatMarginHoldsTests
{
    [Fact]
    public void A_heartbeat_that_fails_once_still_lands_inside_the_skew()
    {
        var worstCase =
            HeartbeatLoop.DefaultInterval +
            BrokerRuntimeStateStore.DefaultRetryBudget +
            HeartbeatLoop.DefaultInterval;

        Assert.True(
            worstCase <= BrokerHeartbeat.MaximumSkew,
            $"A publish that fails once leaves {worstCase} between heartbeats, and a client "
            + $"calls a broker dead after {BrokerHeartbeat.MaximumSkew}. Whichever of the three "
            + "figures moved has to move back, or the skew has to grow with it.");
    }
}
