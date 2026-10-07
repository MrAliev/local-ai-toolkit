using System.Collections.Immutable;
using System.Globalization;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;

namespace LocalAi.Cli;

/// <summary>One report, and whether it was the last one.</summary>
/// <param name="ExitCode">Null while the loop carries on.</param>
public sealed record FollowStep(string Line, int? ExitCode);

/// <summary>
/// What a build embedded, as far as the records seen show: the total each embedding phase counted
/// towards, one per phase and working tree, added up. The base and each overlay embed their own
/// chunks one after another, so their totals add. The total and not the processed count, because
/// a reading once an interval catches a phase part-way and no record after it carries a count
/// (#774). The sync folds every record it writes; a follow loop folds the ones it happened to
/// read, and a phase none of them caught is not in its figure.
/// </summary>
public sealed class EmbeddedChunks
{
    private readonly ImmutableDictionary<(RepositoryIndexProgressPhase Phase, string Root), int> _phases;

    private EmbeddedChunks(
        ImmutableDictionary<(RepositoryIndexProgressPhase Phase, string Root), int> phases) =>
        _phases = phases;

    public static EmbeddedChunks None { get; } =
        new(ImmutableDictionary<(RepositoryIndexProgressPhase Phase, string Root), int>.Empty);

    public int Total => _phases.Values.Sum();

    public EmbeddedChunks After(RepositoryIndexProgress? progress) =>
        progress is not null && progress.Phase.CountsChunks()
            ? new(_phases.SetItem((progress.Phase, progress.WorkingRoot), progress.TotalChunks))
            : this;
}

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

    /// <param name="chunksEmbedded">
    /// <see cref="EmbeddedChunks.Total"/> of the readings so far, for a terminal record from a
    /// build too old to carry <see cref="RepositoryIndexProgress.ChunksEmbedded"/>. The readings
    /// miss a phase that ends between two of them; the build that wrote the record did not.
    /// </param>
    public static FollowStep Step(
        RepositoryIndexProgress? progress,
        TimeSpan sinceLastChange,
        TimeSpan staleAfter,
        int chunksEmbedded = 0)
    {
        if (progress is null)
        {
            return new(CliText.RepoFollowNoBuild, 0);
        }

        if (progress.Phase == RepositoryIndexProgressPhase.Completed)
        {
            var figure = progress.ChunksEmbedded ?? chunksEmbedded;
            return new(
                figure > 0
                    ? CliText.RepoFollowCompleted(Counted(figure))
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

        return new(SyncProgressReporter.Format(progress), null);
    }

    /// <summary>
    /// Figures are quoted verbatim by agents and parsed by tests, so they are invariant wherever
    /// the surrounding sentence is translated.
    /// </summary>
    private static string Counted(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
