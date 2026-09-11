using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>One report, and whether it was the last one.</summary>
/// <param name="ExitCode">Null while the loop carries on.</param>
public sealed record FollowStep(string Line, int? ExitCode);

/// <summary>
/// `repo status --follow`, which reads the progress a build writes and reports it on a cadence.
/// The decision is separated from the waiting so a test can ask what each reading means without
/// asking how fast this machine is.
/// </summary>
public static class RepoFollowCommand
{
    /// <summary>
    /// How long a record may stand still before the loop stops waiting on it. A build flushes
    /// progress every embedding batch — seconds, not minutes — so a record this old belongs to
    /// a build that went away without writing <see cref="RepositoryIndexProgressPhase.Failed"/>.
    /// Waiting on it for ever is the quiet minute that leaves a hung machine looking like a
    /// working one.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The same "try again later" this CLI gives an unreachable broker or a busy sync, and
    /// deliberately not 2: that is what a wrong command line exits with, which is what the
    /// refusals above it use.
    /// </summary>
    public const int BuildDidNotFinish = 75;

    /// <param name="chunksSeen">
    /// The most chunks this loop has watched go by. The terminal record is written by the
    /// step-reporting path, which zeroes the chunk counters on purpose, so it is the only
    /// honest figure a finished build has.
    /// </param>
    public static FollowStep Step(
        RepositoryIndexProgress? progress,
        TimeSpan sinceLastChange,
        TimeSpan staleAfter,
        int chunksSeen = 0)
    {
        if (progress is null)
        {
            return new(CliText.RepoFollowNoBuild, 0);
        }

        if (progress.Phase == RepositoryIndexProgressPhase.Completed)
        {
            return new(
                chunksSeen > 0
                    ? CliText.RepoFollowCompleted(Counted(chunksSeen))
                    : CliText.RepoFollowCompletedPlain,
                0);
        }

        if (progress.Phase == RepositoryIndexProgressPhase.Failed)
        {
            return new(CliText.RepoFollowFailed(progress.WorkingRoot), BuildDidNotFinish);
        }

        if (sinceLastChange > staleAfter)
        {
            return new(
                CliText.RepoFollowStopped(
                    progress.Phase.ToString(),
                    Counted((int)sinceLastChange.TotalSeconds)),
                BuildDidNotFinish);
        }

        return new(Working(progress), null);
    }

    /// <summary>
    /// Figures where the phase counts them, words where it does not — the rule the skill already
    /// states to a reader, and <see cref="RepositoryIndexProgressPhaseExtensions.CountsChunks"/>
    /// is what decides it. Outside embedding the chunk counters are the frozen tally of the last
    /// phase that had one, or zero before any of them ran, and printing those is how a build that
    /// has barely started reads as one that stalled.
    /// </summary>
    private static string Working(RepositoryIndexProgress progress)
    {
        var phase = progress.Phase.ToString();
        if (progress.Phase.CountsChunks() && progress.TotalChunks > 0)
        {
            return CliText.RepoFollowProgress(
                phase,
                Counted(progress.ProcessedChunks),
                Counted(progress.TotalChunks),
                progress.ChunksPerSecond.ToString("0.#", CultureInfo.InvariantCulture),
                progress.EstimatedRemaining is { } left
                    ? Counted((int)left.TotalSeconds)
                    : "?");
        }

        var step = progress.Step?.ToString() ?? phase;
        return progress.StepTotal > 0
            ? CliText.RepoFollowStepProgress(
                phase, step, Counted(progress.StepDone), Counted(progress.StepTotal))
            : CliText.RepoFollowWorking(phase, step);
    }

    /// <summary>
    /// Figures are quoted verbatim by agents and parsed by tests, so they are invariant wherever
    /// the surrounding sentence is translated.
    /// </summary>
    private static string Counted(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
