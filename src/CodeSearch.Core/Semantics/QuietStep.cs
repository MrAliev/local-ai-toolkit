using System.Globalization;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Resources;

namespace CodeSearch.Core.Semantics;

/// <summary>
/// A step with no counter of its own, saying it is alive while it runs.
///
/// <c>dotnet restore</c>, <c>OpenSolutionAsync</c>, <c>scip-typescript</c> and
/// <c>scip-python</c> are each a single call that announces itself, says what it cost, and says
/// nothing in between. A machine that is genuinely stuck looks exactly like one twenty seconds
/// into a restore — or four minutes into an adapter, which the semantic indexing policy allows.
///
/// Public because two assemblies need it. It lived on <c>RoslynSolutionLoader</c> and was internal
/// to this one, so the adapter loop in the console could not reach the ticker that already existed
/// and would have had to grow a second — which is how two of anything come to disagree.
/// </summary>
public static class QuietStep
{
    /// <summary>
    /// Runs <paramref name="step"/> to completion, calling <paramref name="tick"/> once per
    /// interval for as long as it is still going, with the elapsed seconds.
    /// </summary>
    /// <param name="wait">
    /// What a tick costs, as a delegate rather than a sleep: a test passes one that never returns
    /// or returns at once, so what is asserted is when a line appears and not how fast a machine
    /// is.
    /// </param>
    public static async Task<T> WhileItRunsAsync<T>(
        Task<T> step,
        Action<string>? tick,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task> wait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(wait);
        var started = time.GetTimestamp();
        while (true)
        {
            // The delay is abandoned rather than awaited once the step is done, so a step that
            // ends just after a tick does not wait out the rest of the interval.
            var pause = wait(IndexBuilder.TargetBatchDuration, cancellationToken);
            await Task.WhenAny(step, pause).ConfigureAwait(false);
            // The step, not whichever task WhenAny happened to name: when both are complete it
            // returns either, and taking the pause there would announce a step that has finished.
            if (step.IsCompleted)
            {
                return await step.ConfigureAwait(false);
            }

            tick?.Invoke(IndexText.StepStillRunning(
                time.GetElapsedTime(started).TotalSeconds.ToString(
                    "F1",
                    CultureInfo.InvariantCulture)));
        }
    }
}
