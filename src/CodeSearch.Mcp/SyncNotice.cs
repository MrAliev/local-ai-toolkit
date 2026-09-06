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
/// One look at a repository: which repository it is, whether a sync holds its gate right now, and
/// the last progress that sync recorded, if any.
///
/// Both halves are needed and neither is enough. The record alone cannot say whether anything is
/// running — a killed sync leaves its last phase behind for ever, because nothing writes an end on
/// behalf of a process that is gone. The gate alone cannot say how far a build has got.
/// </summary>
public sealed record SyncSighting(
    string RepositoryId,
    bool BuildHoldsTheGate,
    RepositoryIndexProgress? Progress);

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
/// Four states, from two facts that disagree in both directions:
///
/// <list type="bullet">
/// <item>a terminal record — the build ended, whoever heard it start is told so, once;</item>
/// <item>gate held, a record — it is running, and this is the figures line;</item>
/// <item>gate held, no record — it has started and written nothing yet, which is the whole of a
/// first build's first seconds;</item>
/// <item>gate open, a record that is not terminal — it was killed, and nothing will ever finish
/// it.</item>
/// </list>
///
/// Two things this deliberately does not do. It never infers liveness from the record: the age is
/// reported so a killed build reads as `recorded 3.2 h ago`, and whether anything is running is
/// asked of the gate instead. And it never suppresses `STALE`, which is a different fact - the
/// build may be for another worktree, and it may fail.
/// </summary>
public sealed class SyncNotice(Func<string?, SyncSighting?> look)
{
    /// <summary>
    /// What has already been said about each repository's current build. Keyed by the repository
    /// id rather than by the working root, so two worktrees of one repository share one sentence
    /// - they share the build.
    /// </summary>
    private readonly Dictionary<string, SaidSoFar> _said = new(StringComparer.Ordinal);

    private readonly Lock _lock = new();

    /// <summary>
    /// For an answer that would otherwise tell the reader to start a build: the block that goes
    /// where that instruction was, and whether the instruction still belongs under it.
    ///
    /// The block ends on its own last line rather than on a blank one, because on the answer that
    /// says no index exists the instruction was the last thing in the message. It is empty when
    /// there is nothing to say, which leaves that answer exactly as it was.
    ///
    /// One call rather than a line and a separate question about the same repository. Two looks at
    /// a state that moves produce the two answers nobody can act on: a build that ends between
    /// them gives "the build finished" with no command, and one that starts between them names a
    /// command under a line saying it would be refused.
    ///
    /// <see cref="BuildNotice.BuildIsRunning"/> is not quite "would a second sync be refused". In
    /// the moment between a sync stamping its terminal record and letting go of the gate, one
    /// still would be — but withholding the command there would leave a reader holding an
    /// instruction to run a sync and no sync to run, and that moment follows every successful
    /// build.
    /// </summary>
    public BuildNotice InsteadOf(string? root)
    {
        var sighting = look(root);
        return new BuildNotice(
            Compose(sighting, SyncNoticeAudience.Instruction, separated: false),
            sighting is { BuildHoldsTheGate: true } &&
                (sighting.Progress is null || !Ended(sighting.Progress)));
    }

    /// <summary>
    /// The lines to put above an answer, ending in a blank line, or nothing at all.
    /// </summary>
    public string Above(string? root, SyncNoticeAudience audience) =>
        Compose(look(root), audience, separated: true);

    private string Compose(
        SyncSighting? sighting,
        SyncNoticeAudience audience,
        bool separated)
    {
        if (sighting is null)
        {
            return string.Empty;
        }

        lock (_lock)
        {
            _said.TryGetValue(sighting.RepositoryId, out var said);

            // A terminal record outranks the gate. The gate is taken before the first write of
            // shared state and released after the last, so it is still held while a sync stamps
            // Completed and returns - a moment that follows every successful build, and reading it
            // as "a build has started" would announce a beginning to every reader who just saw an
            // end.
            if (sighting.Progress is { } recorded && Ended(recorded))
            {
                return Closing(sighting, said, EndOf(recorded), separated);
            }

            if (!sighting.BuildHoldsTheGate)
            {
                return Stopped(sighting, said, audience, separated);
            }

            var line = "Sync: " +
                (sighting.Progress is { } progress
                    ? Figures(progress, withEta: true)
                    : CodeSearchText.ProgressNotRecordedYet) +
                "\n";
            var explain = audience switch
            {
                // Every time, unlike the sentence above content. This one stands in for a line
                // that was printed every time, and an instruction said once is missing on every
                // call after it: the tenth search on a new machine would read "no index" and
                // offer no route at all.
                SyncNoticeAudience.Instruction => CodeSearchText.BuildAlreadyRunning + "\n",
                SyncNoticeAudience.Answer when !said.ToldWhereAnswersComeFrom =>
                    CodeSearchText.SyncNotSearchableYet + "\n",
                _ => string.Empty,
            };

            _said[sighting.RepositoryId] = said with
            {
                Announced = true,
                ToldWhereAnswersComeFrom =
                    said.ToldWhereAnswersComeFrom || audience is SyncNoticeAudience.Answer,
            };
            return line + explain + Separator(separated);
        }
    }

    /// <summary>
    /// A record that is not terminal over an open gate: a sync that was killed. Nothing will
    /// finish it and nothing will clear it, which is what decides the split.
    ///
    /// On an answer that names a command the figures print every time — the reader asked why there
    /// is no index, and "your last attempt stopped here" is the answer. On an answer that returns
    /// content one sentence prints, once, and only to a caller holding the promise of a finish
    /// that is not coming: a line printed unconditionally would ride above every search answer for
    /// as long as nobody re-syncs, which may be weeks.
    /// </summary>
    private string Stopped(
        SyncSighting sighting,
        SaidSoFar said,
        SyncNoticeAudience audience,
        bool separated)
    {
        if (sighting.Progress is not { } progress)
        {
            return string.Empty;
        }

        return audience is SyncNoticeAudience.Instruction
            ? "Sync: " +
                CodeSearchText.ProgressStoppedAt(Figures(progress, withEta: false)) +
                "\n" + Separator(separated)
            : Closing(sighting, said, CodeSearchText.SyncDidNotFinish, separated);
    }

    /// <summary>
    /// The last thing said about a build, to whoever was told it was running. Announcing the end
    /// of a build nobody heard start is noise, and it would arrive on the first call of every
    /// session.
    /// </summary>
    private string Closing(
        SyncSighting sighting,
        SaidSoFar said,
        string sentence,
        bool separated)
    {
        if (!said.Announced)
        {
            return string.Empty;
        }

        _said.Remove(sighting.RepositoryId);
        return "Sync: " + sentence + "\n" + Separator(separated);
    }

    private static string EndOf(RepositoryIndexProgress progress) =>
        progress.Phase is RepositoryIndexProgressPhase.Failed
            ? CodeSearchText.SyncStopped
            : CodeSearchText.SyncEnded;

    /// <summary>
    /// A recorded phase past which nothing more will be written. Not a claim that the process is
    /// gone: it is what the last write said, which is all the record ever says.
    /// </summary>
    private static bool Ended(RepositoryIndexProgress progress) =>
        progress.Phase is RepositoryIndexProgressPhase.Completed
            or RepositoryIndexProgressPhase.Failed;

    /// <summary>
    /// Phase, figures and age. The figures use the keys `index_status` prints, so a reader who
    /// sees both never sees two accounts of one build.
    ///
    /// <paramref name="withEta"/> is false for a build that is no longer running: the estimate was
    /// the dead process's prediction of a remaining time nobody is spending, and it is the only
    /// field here that is a prediction rather than a dated fact.
    /// </summary>
    private static string Figures(RepositoryIndexProgress progress, bool withEta)
    {
        var counted = progress.Phase.CountsChunks()
            ? CodeSearchText.ProgressChunks(
                  progress.ProcessedChunks,
                  progress.TotalChunks,
                  Math.Max(0, progress.TotalChunks - progress.ProcessedChunks)) +
              (withEta ? ", " + Eta(progress.EstimatedRemaining) : string.Empty)
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
    /// The age in the unit a reader reads, and never negative: a clock that disagrees with the
    /// writer of the file by a second would otherwise report the future.
    ///
    /// It climbs all the way to days because this is the one figure here that is routinely large.
    /// A build whose process died leaves a record nothing ever clears, so the honest report of it
    /// is `recorded 2.4 d ago` rather than `recorded 3456.0 min ago`.
    /// </summary>
    private static string Age(TimeSpan since)
    {
        var age = since < TimeSpan.Zero ? TimeSpan.Zero : since;
        return age switch
        {
            _ when age < TimeSpan.FromMinutes(1) => CodeSearchText.Seconds(
                age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)),
            _ when age < TimeSpan.FromHours(1) => CodeSearchText.EtaMinutes(
                age.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)),
            _ when age < TimeSpan.FromDays(1) => CodeSearchText.Hours(
                age.TotalHours.ToString("F1", CultureInfo.InvariantCulture)),
            _ => CodeSearchText.Days(
                age.TotalDays.ToString("F1", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>
    /// The blank line under the block, when an answer follows it. Nothing when the block is the
    /// last thing in the message.
    /// </summary>
    private static string Separator(bool separated) => separated ? "\n" : string.Empty;

    private readonly record struct SaidSoFar(
        bool Announced,
        bool ToldWhereAnswersComeFrom);
}

/// <summary>
/// What an answer that names a build command needs to know, from one look: what to print where the
/// instruction was, and whether the instruction still belongs under it.
/// </summary>
public readonly record struct BuildNotice(string Above, bool BuildIsRunning);
