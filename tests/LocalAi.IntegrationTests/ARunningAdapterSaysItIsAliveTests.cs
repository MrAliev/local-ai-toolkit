using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// `scip-typescript` and `scip-python` are each a single awaited call, and the semantic indexing
/// policy allows either of them three hundred seconds. The step was named once before the call and
/// nothing was written until it returned, so `recorded 4 min ago` grew through a perfectly healthy
/// run — the same indistinguishability the pause reporting was added to remove, with a better
/// label on it.
///
/// Neither test waits on a clock. The pause between ticks is a delegate, and these pass ones that
/// either never return or return at once: what is under test is when the record is stamped and
/// when it is not, never how long anything took.
/// </summary>
public sealed class ARunningAdapterSaysItIsAliveTests
{
    /// <summary>
    /// An adapter that outlasts the interval stamps the record once per interval, under the step
    /// it is running — the name is what makes the age beside it readable rather than alarming.
    /// </summary>
    [Fact]
    public async Task An_adapter_that_outlasts_the_interval_keeps_the_record_moving()
    {
        var adapter = new TaskCompletionSource<int>();
        var stamped = new List<IndexBuildStep>();
        var ticks = 0;

        Task Wait(TimeSpan _, CancellationToken __)
        {
            // The adapter ends on the third tick, so the count is decided here rather than by a
            // race: the loop checks the adapter before stamping, so no fourth stamp can follow.
            if (++ticks == 3)
            {
                adapter.SetResult(7);
            }

            return Task.CompletedTask;
        }

        var result = await CodeSearchSyncCommand.WhileTheAdapterRunsAsync(
            adapter.Task,
            IndexBuildStep.ScipTypeScript,
            (step, _, _) => stamped.Add(step),
            TimeProvider.System,
            Wait,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal(
            [IndexBuildStep.ScipTypeScript, IndexBuildStep.ScipTypeScript],
            stamped);
    }

    /// <summary>
    /// And one that finishes inside the interval stamps nothing extra. That is the common case on
    /// a repository with a handful of TypeScript files, and it is what keeps the stamps meaning
    /// "still going" rather than becoming noise every run carries.
    /// </summary>
    [Fact]
    public async Task An_adapter_that_finishes_first_stamps_nothing()
    {
        var stamped = new List<IndexBuildStep>();

        var result = await CodeSearchSyncCommand.WhileTheAdapterRunsAsync(
            Task.FromResult(11),
            IndexBuildStep.ScipPython,
            (step, _, _) => stamped.Add(step),
            TimeProvider.System,
            (_, token) => Task.Delay(Timeout.Infinite, token),
            TestContext.Current.CancellationToken);

        Assert.Equal(11, result);
        Assert.Empty(stamped);
    }
}
