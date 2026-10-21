using System.Diagnostics;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class GitHubCliLocatorBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_returns_while_lookup_is_still_blocked_and_discards_its_late_result(bool deadline)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromMinutes(1)); // deadlock protection, no speed assertion
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ControlledClock();
        var running = Task.Run(() => GitHubCliUpdateSource.ExecuteWithLocatorAsync([], 64, caller.Token, _ =>
        {
            entered.TrySetResult();
            try
            {
                release.Wait(); // model an uninterruptible native filesystem lookup
                return new ProcessStartInfo(); // inert: no executable can be launched even on regression
            }
            finally { finished.TrySetResult(); }
        }, TimeSpan.FromSeconds(30), clock), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(watchdog.Token);
            if (deadline)
            {
                clock.Expire();
                var exception = await Assert.ThrowsAsync<UpdateCheckSourceException>(() => running.WaitAsync(watchdog.Token));
                Assert.Equal(UpdateCheckFailure.Timeout, exception.Failure);
            }
            else
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(watchdog.Token));
                Assert.True(running.IsCanceled);
            }
            Assert.False(release.IsSet); // completion did not depend on lookup finishing
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(watchdog.Token);
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private ControlledTimer? timer;
        public void Expire() => (timer ?? throw new InvalidOperationException("Deadline was not armed.")).Expire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new ControlledTimer(callback, state);

        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Expire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
