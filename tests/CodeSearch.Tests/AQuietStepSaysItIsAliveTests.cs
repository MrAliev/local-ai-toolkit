using CodeSearch.Core.Semantics;

namespace CodeSearch.Tests;

/// <summary>
/// `dotnet restore` and `OpenSolutionAsync` are single calls with no per-project callback — about
/// 21 s and 10 s on the run that produced #387 — so each announced itself, said what it cost, and
/// said nothing in between. A machine that is genuinely stuck looked exactly like one twenty
/// seconds into a restore (#419).
///
/// Neither test waits: the pause between lines is a delegate, and these pass ones that either
/// never return or return at once. What is under test is when the line appears and when it does
/// not, never how long anything took.
/// </summary>
public sealed class AQuietStepSaysItIsAliveTests
{
    /// <summary>
    /// The property the issue asks to pin: a step that finishes inside the interval prints
    /// nothing at all. That is the common case on a warm machine, and it is what makes the line's
    /// presence information rather than noise.
    ///
    /// The pause here never returns, so the only way out is the step finishing — no clock is
    /// consulted and nothing pretends this machine is fast.
    /// </summary>
    [Fact]
    public async Task A_step_that_finishes_first_says_nothing()
    {
        var lines = new List<string>();

        var result = await QuietStep.WhileItRunsAsync(
            Task.FromResult(7),
            lines.Add,
            TimeProvider.System,
            (_, token) => Task.Delay(Timeout.Infinite, token),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Empty(lines);
    }

    /// <summary>
    /// And a step that outlasts the interval says so, once per interval, carrying a time and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task A_step_that_outlasts_the_interval_says_it_is_still_running()
    {
        var step = new TaskCompletionSource<int>();
        var lines = new List<string>();

        // The step ends after the second line, so the count is decided by the test rather than by
        // a race: the loop checks the step before speaking, so no third line can follow.
        void Say(string line)
        {
            lines.Add(line);
            if (lines.Count == 2)
            {
                step.SetResult(7);
            }
        }

        var result = await QuietStep.WhileItRunsAsync(
            step.Task,
            Say,
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("Still running,", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// A step nobody is listening to still runs. The loader passes a null sink when the caller
    /// asked for no diagnostics, and that must not be a null reference in the middle of a restore.
    /// </summary>
    [Fact]
    public async Task A_step_with_nowhere_to_report_still_finishes()
    {
        var step = new TaskCompletionSource<int>();
        var ticks = 0;

        var result = await QuietStep.WhileItRunsAsync(
            step.Task,
            null,
            TimeProvider.System,
            (_, _) =>
            {
                if (++ticks == 2)
                {
                    step.SetResult(7);
                }

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result);
    }
}
