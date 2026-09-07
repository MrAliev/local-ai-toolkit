using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// Three numbers in three assemblies decide whether a working broker is mistaken for a dead one,
/// and none of them mentioned the others: the loop writes a heartbeat every second, a write spends
/// some budget retrying a rename a reader is denying, and a client treats a heartbeat older than
/// five seconds as a dead broker (#396).
///
/// With the store's own two-second budget the margin bought exactly one failed write between
/// heartbeats — four seconds against five — and two in a row needed seven. The heartbeat now
/// spends its own, shorter budget instead, because it is the one caller with a second chance a
/// second later: three consecutive failures fit, a fourth does not.
///
/// This pins the guarantee rather than the limit: widening the margin should not fail a test.
/// </summary>
public sealed class TheHeartbeatMarginHoldsTests
{
    [Fact]
    public void Three_consecutive_failed_publishes_still_land_inside_the_skew()
    {
        Assert.True(
            GapAfter(3) <= BrokerHeartbeat.MaximumSkew,
            $"Three failed writes leave {GapAfter(3)} between heartbeats, and a client calls a "
            + $"broker dead after {BrokerHeartbeat.MaximumSkew}. Whichever of the three figures "
            + "moved has to move back, or the skew has to grow with it.");
    }

    /// <summary>
    /// The heartbeat gives up on a contended rename sooner than a caller who publishes once,
    /// which is the whole of what buys the margin. Pinned so that collapsing the two budgets back
    /// into one is a test failure rather than a quiet loss of two failures' worth of room.
    /// </summary>
    [Fact]
    public void The_heartbeat_gives_up_sooner_than_a_caller_with_no_second_chance()
    {
        Assert.True(
            HeartbeatLoop.PublishRetryBudget < BrokerRuntimeStateStore.DefaultRetryBudget,
            $"The heartbeat's budget is {HeartbeatLoop.PublishRetryBudget} and the default is "
            + $"{BrokerRuntimeStateStore.DefaultRetryBudget}.");
    }

    /// <summary>
    /// From one successful heartbeat to the next, when the writes in between fail: the loop waits
    /// a tick before every attempt, and each failed attempt spends its whole budget before
    /// throwing.
    /// </summary>
    private static TimeSpan GapAfter(int failedPublishes) =>
        HeartbeatLoop.DefaultInterval * (failedPublishes + 1) +
        HeartbeatLoop.PublishRetryBudget * failedPublishes;
}
