using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>One report, and whether it was the last one.</summary>
/// <param name="ExitCode">Null while the loop carries on.</param>
public sealed record FollowStep(string Line, int? ExitCode);

/// <summary>
/// `repo status --follow`, which reads the progress a build writes on every batch and reports
/// it on a cadence. The decision is separated from the waiting so a test can ask what each
/// reading means without asking how fast this machine is.
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

    public static FollowStep Step(
        RepositoryIndexProgress? progress,
        TimeSpan sinceLastChange,
        TimeSpan staleAfter)
    {
        if (progress is null)
        {
            return new(CliText.RepoFollowNoBuild, 0);
        }

        if (progress.Phase == RepositoryIndexProgressPhase.Completed)
        {
            return new(CliText.RepoFollowCompleted(Counted(progress.ProcessedChunks)), 0);
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

        return new(
            CliText.RepoFollowProgress(
                progress.Phase.ToString(),
                Counted(progress.ProcessedChunks),
                Counted(progress.TotalChunks),
                progress.ChunksPerSecond.ToString("0.#", CultureInfo.InvariantCulture),
                progress.EstimatedRemaining is { } left
                    ? Counted((int)left.TotalSeconds)
                    : "?"),
            null);
    }

    /// <summary>
    /// Figures are quoted verbatim by agents and parsed by tests, so they are invariant wherever
    /// the surrounding sentence is translated.
    /// </summary>
    private static string Counted(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
