using System.Globalization;
using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Mcp;

/// <summary>
/// What kind of answer the line is going above, which decides the sentence under the figures.
/// </summary>
public enum SyncNoticeAudience
{
    /// <summary>
    /// An answer carrying content — hits, locations, a chunk. What the reader needs to know is
    /// where the content came from while the build is unfinished.
    /// </summary>
    Answer,

    /// <summary>
    /// An answer that tells the reader to start a build. That instruction is wrong while one is
    /// running, so it is replaced rather than qualified.
    /// </summary>
    Instruction,
}

/// <summary>
/// The line a build in flight puts above every answer it would change.
///
/// The figures existed and crossed process boundaries long before this: `index_status` has
/// printed them from `progress.json` for as long as the file has been written. What was missing
/// is that nobody saw them without asking - and the reader who most needs them, somebody watching
/// a first build on a new machine, is exactly the one who does not know to ask. Worse, until this
/// existed that reader was told to start a build that was already running, and the second
/// `localai sync` would have exited 75 against the gate.
///
/// Two things this deliberately does not do. It never infers liveness: it reports the recorded
/// phase, the recorded figures and how long ago they were recorded, so a killed build reads as
/// `recorded 3.2 h ago` rather than as a running one. And it never suppresses `STALE`, which is a
/// different fact - the build may be for another worktree, and it may fail.
/// </summary>
public sealed class SyncNotice(Func<string?, RepositoryIndexProgress?> read)
{
    /// <summary>
    /// What has already been said about each repository's current build. Keyed by the repository
    /// id rather than by the working root, so two worktrees of one repository share one sentence
    /// - they share the build.
    /// </summary>
    private readonly Dictionary<string, SaidSoFar> _said = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    /// <summary>
    /// The lines to put above an answer, ending in a blank line, or nothing at all.
    /// </summary>
    public string Above(string? root, SyncNoticeAudience audience)
    {
        var progress = read(root);
        if (progress is null)
        {
            return string.Empty;
        }

        var ended = progress.Phase is RepositoryIndexProgressPhase.Completed
            or RepositoryIndexProgressPhase.Failed;
        lock (_gate)
        {
            _said.TryGetValue(progress.RepositoryId, out var said);
            if (ended)
            {
                // Only to a caller that was told a build was running. Announcing the end of a
                // build nobody heard start is noise.
                if (!said.Announced)
                {
                    return string.Empty;
                }

                _said.Remove(progress.RepositoryId);
                return "Sync: " +
                    (progress.Phase is RepositoryIndexProgressPhase.Failed
                        ? CodeSearchText.SyncStopped
                        : CodeSearchText.SyncEnded) +
                    "\n\n";
            }

            var line = "Sync: " + Line(progress) + "\n";
            var explain = audience switch
            {
                SyncNoticeAudience.Instruction when !said.ToldWhereToBuild =>
                    CodeSearchText.BuildAlreadyRunning + "\n",
                SyncNoticeAudience.Answer when !said.ToldWhereAnswersComeFrom =>
                    CodeSearchText.SyncNotSearchableYet + "\n",
                _ => string.Empty,
            };

            _said[progress.RepositoryId] = said with
            {
                Announced = true,
                ToldWhereToBuild =
                    said.ToldWhereToBuild || audience is SyncNoticeAudience.Instruction,
                ToldWhereAnswersComeFrom =
                    said.ToldWhereAnswersComeFrom || audience is SyncNoticeAudience.Answer,
            };
            return line + explain + "\n";
        }
    }

    /// <summary>
    /// Phase, figures and age. The figures use the keys `index_status` prints, so a reader who
    /// sees both never sees two accounts of one build.
    /// </summary>
    private static string Line(RepositoryIndexProgress progress)
    {
        var counted = progress.Phase.CountsChunks()
            ? CodeSearchText.ProgressChunks(
                  progress.ProcessedChunks,
                  progress.TotalChunks,
                  Math.Max(0, progress.TotalChunks - progress.ProcessedChunks)) +
              ", " + Eta(progress.EstimatedRemaining)
            : CodeSearchText.ProgressNotCounted;

        return $"{progress.Phase} - {counted}, " +
            CodeSearchText.RecordedAgo(Age(DateTimeOffset.UtcNow - progress.UpdatedAtUtc));
    }

    private static string Eta(TimeSpan? remaining) =>
        remaining is { } estimate
            ? "ETA " + CodeSearchText.EtaMinutes(
                  estimate.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture))
            : CodeSearchText.EtaCalculating;

    /// <summary>
    /// Seconds under a minute, minutes above it, and never negative: a clock that disagrees with
    /// the writer of the file by a second would otherwise report the future.
    /// </summary>
    private static string Age(TimeSpan since)
    {
        var age = since < TimeSpan.Zero ? TimeSpan.Zero : since;
        return age < TimeSpan.FromMinutes(1)
            ? CodeSearchText.Seconds(age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture))
            : CodeSearchText.EtaMinutes(
                age.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture));
    }

    private readonly record struct SaidSoFar(
        bool Announced,
        bool ToldWhereToBuild,
        bool ToldWhereAnswersComeFrom);
}
