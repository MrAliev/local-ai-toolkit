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
    RepositoryIndexProgress? Progress)
{
    /// <summary>
    /// A recorded phase past which nothing more will be written. Not a claim that the process is
    /// gone: it is what the last write said, which is all the record ever says.
    /// </summary>
    public bool Ended =>
        Progress is
        {
            Phase: RepositoryIndexProgressPhase.Completed
                or RepositoryIndexProgressPhase.Failed,
        };

    /// <summary>
    /// Whether a build is working right now. Not quite "would a second sync be refused": in the
    /// moment between a sync stamping its terminal record and letting go of the gate, one still
    /// would be — but that moment follows every successful build, and treating it as a running
    /// build would withhold a command from a reader who has no sync to wait for.
    /// </summary>
    public bool BuildIsRunning => BuildHoldsTheGate && !Ended;
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
            sighting is { BuildIsRunning: true });
    }

    /// <summary>
    /// The block naming a running build, from one look, or null when none is running. Nothing is
    /// composed for null, so an end notice owed to the next answer is not spent here.
    /// </summary>
    public string? WhileRunning(string? root)
    {
        var sighting = look(root);
        return sighting is { BuildIsRunning: true }
            ? Compose(sighting, SyncNoticeAudience.Instruction, separated: false)
            : null;
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
            if (sighting is { Ended: true, Progress: { } recorded })
            {
                return Closing(sighting, said, EndOf(recorded), separated);
            }

            if (!sighting.BuildHoldsTheGate)
            {
                return Stopped(sighting, said, audience, separated);
            }

            var line = "Sync: " +
                (sighting.Progress is { } progress
                    ? Figures(progress, running: true)
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
                CodeSearchText.ProgressStoppedAt(Figures(progress, running: false)) +
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
    /// Phase, figures and age. The figures use the keys `index_status` prints, so a reader who
    /// sees both never sees two accounts of one build.
    ///
    /// <paramref name="running"/> is false for a build that is no longer running, and it gates the
    /// two fields that are only true of one that is. The estimate was the dead process's
    /// prediction of a remaining time nobody is spending. The pause is the same kind of leftover:
    /// the field stays in the record for ever, exactly as the phase does, and printing it there
    /// would report a build that is gone as waiting for something to come back.
    ///
    /// A pause takes the estimate's slot rather than standing beside it. A waiting build writes
    /// rate 0 and no estimate by construction, so the slot would otherwise always read
    /// `calculating` — which says an estimate is being worked out while nothing is moving, and
    /// that is the sentence this line was missing: frozen counters under `calculating`, beside a
    /// `recorded 4 s ago` the wait keeps fresh on every rung of its ladder, made a backend outage
    /// and a dying build read exactly alike. `index_status` has said `Paused: BackendUnavailable`
    /// since the reason existed; this line, which rides above every answer and is read by whoever
    /// is watching without asking, did not.
    /// </summary>
    private static string Figures(RepositoryIndexProgress progress, bool running)
    {
        var pause = running ? progress.Pause : null;
        // Chunks where the phase counts them, and otherwise what the phase is doing. The guard is
        // the same one it always was: the chunk pair belongs to embedding, and a finished tally
        // printed beside a phase that is not counting is what made a build with minutes left read
        // as one that had finished and stalled.
        var doing = progress.Phase.CountsChunks()
            ? CodeSearchText.ProgressChunks(
                  progress.ProcessedChunks,
                  progress.TotalChunks,
                  Math.Max(0, progress.TotalChunks - progress.ProcessedChunks)) +
              (running && pause is null ? ", " + Eta(progress.EstimatedRemaining) : string.Empty)
            : ProgressStepText.Of(progress);

        return $"{progress.Phase} - {doing}" +
            (pause is { } waiting ? ", " + Waiting(waiting) : string.Empty) +
            ", " + CodeSearchText.RecordedAgo(RecordedAge.Of(progress.UpdatedAtUtc));
    }

    /// <summary>
    /// One clause per pause, chosen by the member rather than by printing the token: the row name
    /// `Paused:` supplies the word on `index_status`, and here the only field name is the literal
    /// `Sync: ` prefix, so the word has to open the value.
    ///
    /// A second member of the enum throws here rather than printing nothing. C# cannot make that
    /// a build error — an enum value need not be one of the named ones, so a switch expression
    /// without this arm is rejected as non-exhaustive (CS8524) — and a silent empty clause is the
    /// worse failure of the two: it looks exactly like the build that is not paused at all, which
    /// is the state this whole line exists to distinguish.
    /// </summary>
    private static string Waiting(IndexBuildPause pause) => pause switch
    {
        IndexBuildPause.BackendUnavailable => CodeSearchText.ProgressPausedBackendUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(pause), pause, null),
    };

    private static string Eta(TimeSpan? remaining) =>
        remaining is { } estimate
            ? "ETA " + CodeSearchText.EtaMinutes(
                  estimate.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture))
            : CodeSearchText.EtaCalculating;

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
