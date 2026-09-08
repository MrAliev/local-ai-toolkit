using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// What a build in flight says above the answers it would change.
///
/// The figures were never missing - `index_status` has printed them from the same file for as
/// long as it has been written. What was missing is that nobody saw them without asking, and the
/// reader who most needs them is the one who does not know to ask: somebody watching a first
/// build on a new machine, who until now was told to start a build that was already running.
///
/// Two sources, because neither alone is enough. The record says how far a build got; only the
/// gate says whether anything is running, because a killed sync leaves its last phase behind for
/// ever - nothing writes an end on behalf of a process that is gone.
///
/// Asserted against the catalogue rather than against English words: these lines are printed in
/// the reader's language and the tests have to hold in both.
/// </summary>
public sealed class SyncNoticeTests
{
    private const string Repository = "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966a";

    [Fact]
    public void A_counting_phase_says_the_phase_the_chunks_and_how_long_ago()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

        var above = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.StartsWith("Sync: EmbeddingBase - ", above, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.ProgressChunks(948, 1511, 563), above, StringComparison.Ordinal);
        Assert.Contains("ETA ", above, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.SyncNotSearchableYet, above, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", above, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build waiting for embeddings and a build dying read exactly alike on this line.
    ///
    /// `index_status` has printed `Paused: BackendUnavailable` since the reason existed. This line
    /// — the one that rides above every search and navigation answer, read by whoever is watching
    /// without asking — dropped it. What was left was the counters frozen at the batch the outage
    /// began on, a fresh `recorded 4 s ago` beside them, because the wait rewrites the record on
    /// every rung of the ladder, and `ETA calculating`, which says an estimate is being worked out
    /// while nothing is moving. The clause takes the estimate's slot rather than joining it: a
    /// paused build writes rate 0 and no estimate by construction, so the slot could hold nothing
    /// else.
    /// </summary>
    [Fact]
    public void A_paused_build_says_it_is_waiting_rather_than_calculating()
    {
        var notice = Watching(Paused(RepositoryIndexProgressPhase.EmbeddingBase, 3120, 8250));

        var above = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(
            CodeSearchText.ProgressChunks(3120, 8250, 5130),
            above,
            StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.ProgressPausedBackendUnavailable,
            above,
            StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.EtaCalculating, above, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pause on a record nothing will ever finish is not a wait: the process that would have
    /// resumed is gone, and it left the field behind the way it left the phase behind.
    /// `ProgressStoppedAt` owns that state, and the clause under it would report a dead build as
    /// waiting for something to come back — the same reading the dropped ETA is dropped to prevent.
    /// </summary>
    [Fact]
    public void A_pause_left_behind_by_a_build_that_died_is_not_reported_as_waiting()
    {
        var notice = Watching(StoppedWhilePaused(
            RepositoryIndexProgressPhase.EmbeddingBase,
            3120,
            8250,
            TimeSpan.FromHours(3.2)));

        var instead = notice.InsteadOf("R:\\LocalAi").Above;

        Assert.Contains(
            CodeSearchText.ProgressChunks(3120, 8250, 5130),
            instead,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.ProgressPausedBackendUnavailable,
            instead,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence explains; the figures report. Repeating the sentence on every call of a build
    /// that lasts minutes would train a reader to skip the whole line, figures included.
    /// </summary>
    [Fact]
    public void The_sentence_is_said_once_and_the_figures_every_time()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

        var first = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);
        var second = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.SyncNotSearchableYet, first, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.SyncNotSearchableYet, second, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.ProgressChunks(948, 1511, 563), second, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect this exists for: on a new machine every search says "no index, build it" for the
    /// whole first build, and that second build would be refused by the gate.
    /// </summary>
    [Fact]
    public void An_answer_that_says_build_it_is_told_a_build_is_already_running()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.EmbeddingBase, 12, 26480));

        var above = notice.InsteadOf("R:\\IntelWash").Above;

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, above, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.SyncNotSearchableYet, above, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opposite of the sentence above content, and for the opposite reason. A caveat repeated
    /// over an answer the reader wanted trains them to skip the line; a replacement for an
    /// instruction is the whole answer, and an instruction said once is missing every time after.
    /// The tenth search on a new machine would otherwise read "no index" and offer no route at
    /// all - which is the failure this exists to prevent, arriving one call later than before.
    /// </summary>
    [Fact]
    public void The_instruction_it_stands_in_for_is_said_every_time()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.EmbeddingBase, 12, 26480));

        var first = notice.InsteadOf("R:\\IntelWash").Above;
        var tenth = string.Empty;
        for (var call = 0; call < 9; call++)
        {
            tenth = notice.InsteadOf("R:\\IntelWash").Above;
        }

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, first, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.BuildAlreadyRunning, tenth, StringComparison.Ordinal);
    }

    /// <summary>
    /// The blank line is a separator, and a separator with nothing after it is noise. This block
    /// takes the slot an instruction had, and on the no-index answer the instruction was the last
    /// thing in the message.
    /// </summary>
    [Fact]
    public void A_block_that_stands_in_for_an_instruction_ends_without_a_blank_line()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.EmbeddingBase, 12, 26480));

        var instead = notice.InsteadOf("R:\\IntelWash").Above;

        Assert.EndsWith("\n", instead, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", instead, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_happening_the_instruction_is_left_alone()
    {
        Assert.Equal(string.Empty, Watching(Idle()).InsteadOf("R:\\IntelWash").Above);
    }

    /// <summary>
    /// The window a new machine opens in: the gate is taken before the first byte of shared state
    /// is written, so between a sync starting and its first record there is a build with no
    /// figures. Saying nothing there would delay the reader's only warning past the answer it was
    /// about, and would leave "Until it finishes" with nothing to refer to.
    /// </summary>
    [Fact]
    public void A_build_that_has_started_and_recorded_nothing_says_so()
    {
        var notice = Watching(new SyncSighting(Repository, BuildHoldsTheGate: true, null));

        var above = notice.Above("R:\\IntelWash", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.ProgressNotRecordedYet, above, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.SyncNotSearchableYet, above, StringComparison.Ordinal);
        Assert.DoesNotContain("ETA", above, StringComparison.Ordinal);
    }

    /// <summary>
    /// A phase that counts nothing says so. Printing the previous phase's finished tally beside it
    /// is what made a build with minutes left read as one that had finished and stalled.
    /// </summary>
    [Fact]
    public void A_phase_that_counts_nothing_says_so_rather_than_showing_a_stale_tally()
    {
        var notice = Watching(Running(RepositoryIndexProgressPhase.SemanticBase, 1511, 1511));

        var above = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.ProgressNotCounted, above, StringComparison.Ordinal);
        Assert.DoesNotContain("1511", above, StringComparison.Ordinal);
    }

    /// <summary>
    /// Having told a caller that answers come from the last published index, the product owes it
    /// the moment that stops being true - once, and then silence.
    /// </summary>
    [Fact]
    public void The_end_of_a_build_is_announced_once_to_whoever_heard_it_start()
    {
        var sighting = Running(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511);
        var notice = Watching(_ => sighting);
        notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        sighting = Finished(RepositoryIndexProgressPhase.Completed);
        var ended = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);
        var after = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.SyncEnded, ended, StringComparison.Ordinal);
        Assert.Equal(string.Empty, after);
    }

    /// <summary>
    /// And never to a caller that heard nothing: announcing the end of a build nobody was told
    /// about is noise, and it would arrive on the first call of every session.
    /// </summary>
    [Fact]
    public void A_build_nobody_was_told_about_ends_quietly()
    {
        var notice = Watching(Finished(RepositoryIndexProgressPhase.Completed));

        Assert.Equal(string.Empty, notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer));
    }

    /// <summary>
    /// The gate is taken before the first shared write and released after the last, so between a
    /// sync stamping Completed and returning there is a moment where the gate is held and the
    /// record is terminal. That moment happens after every successful sync, and reading it as
    /// "a build has started" would announce a beginning to every reader who just saw an end.
    /// The record wins there: it says this build reached its end, and the gate says only that a
    /// thread has not been joined yet.
    /// </summary>
    [Fact]
    public void A_finished_record_outranks_a_gate_that_has_not_been_released_yet()
    {
        var sighting = Running(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511);
        var notice = Watching(_ => sighting);
        notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        sighting = new SyncSighting(
            Repository,
            BuildHoldsTheGate: true,
            Progress(RepositoryIndexProgressPhase.Completed, 1511, 1511, TimeSpan.Zero));
        var ended = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.SyncEnded, ended, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.ProgressNotRecordedYet,
            ended,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_recorded_and_nothing_running_says_nothing()
    {
        Assert.Equal(
            string.Empty,
            Watching(Idle()).Above("R:\\LocalAi", SyncNoticeAudience.Answer));
    }

    /// <summary>
    /// A killed sync: the record is not terminal and never will be, because nothing writes an end
    /// for a process that is gone. The reader on the no-index path needs the command back - the
    /// gate is open and the sync will work - and the figures are the answer to the question they
    /// actually have, which is why there is no index. The ETA is dropped: it was the dead
    /// process's prediction of a remaining time nobody is spending.
    /// </summary>
    [Fact]
    public void A_build_that_died_says_where_it_stopped_and_predicts_nothing()
    {
        var notice = Watching(Stopped(
            RepositoryIndexProgressPhase.EmbeddingBase,
            3182,
            26480,
            TimeSpan.FromHours(3.2)));

        var instead = notice.InsteadOf("R:\\IntelWash").Above;

        Assert.Contains(
            CodeSearchText.ProgressChunks(3182, 26480, 23298),
            instead,
            StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.Hours("3.2")),
            instead,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ETA", instead, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.BuildAlreadyRunning,
            instead,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// On an answer that returns content the same record is worth one sentence and no more, and
    /// only to a caller holding the promise of a finish that is not coming. Nothing clears that
    /// record, so a line printed unconditionally would ride above every search answer for weeks
    /// and teach the reader to skip the one line that matters.
    /// </summary>
    [Fact]
    public void A_death_is_announced_once_to_whoever_heard_the_build_start()
    {
        var sighting = Running(RepositoryIndexProgressPhase.EmbeddingBase, 3182, 26480);
        var notice = Watching(_ => sighting);
        notice.Above("R:\\IntelWash", SyncNoticeAudience.Answer);

        sighting = Stopped(
            RepositoryIndexProgressPhase.EmbeddingBase,
            3182,
            26480,
            TimeSpan.FromHours(3.2));
        var died = notice.Above("R:\\IntelWash", SyncNoticeAudience.Answer);
        var after = notice.Above("R:\\IntelWash", SyncNoticeAudience.Answer);

        Assert.Contains(CodeSearchText.SyncDidNotFinish, died, StringComparison.Ordinal);
        Assert.Equal(string.Empty, after);
    }

    [Fact]
    public void A_build_nobody_was_told_about_dies_quietly()
    {
        var notice = Watching(Stopped(
            RepositoryIndexProgressPhase.EmbeddingBase,
            3182,
            26480,
            TimeSpan.FromHours(3.2)));

        Assert.Equal(
            string.Empty,
            notice.Above("R:\\IntelWash", SyncNoticeAudience.Answer));
    }

    /// <summary>
    /// What the callers that name a command actually ask: is a build working right now. Not "would
    /// a second sync be refused" to the millisecond - during the moment between a terminal record
    /// and the gate's release a second sync would still be refused, but withholding the command
    /// there would leave a reader with an instruction to run a sync and no sync to run.
    /// </summary>
    [Theory]
    [InlineData(true, RepositoryIndexProgressPhase.EmbeddingBase, true)]
    [InlineData(true, RepositoryIndexProgressPhase.SemanticBase, true)]
    [InlineData(true, RepositoryIndexProgressPhase.Completed, false)]
    [InlineData(true, RepositoryIndexProgressPhase.Failed, false)]
    [InlineData(false, RepositoryIndexProgressPhase.EmbeddingBase, false)]
    public void A_caller_that_names_a_command_can_ask_whether_one_is_already_running(
        bool holdsTheGate,
        RepositoryIndexProgressPhase phase,
        bool expected)
    {
        var notice = Watching(new SyncSighting(
            Repository,
            holdsTheGate,
            Progress(phase, 948, 1511, TimeSpan.Zero)));

        Assert.Equal(expected, notice.InsteadOf("R:\\LocalAi").BuildIsRunning);
    }

    [Fact]
    public void A_build_that_has_started_and_recorded_nothing_is_running()
    {
        var notice = Watching(new SyncSighting(Repository, BuildHoldsTheGate: true, null));

        Assert.True(notice.InsteadOf("R:\\LocalAi").BuildIsRunning);
    }

    [Fact]
    public void Nothing_at_all_is_no_build_running()
    {
        Assert.False(Watching(Idle()).InsteadOf("R:\\LocalAi").BuildIsRunning);
        Assert.False(new SyncNotice(_ => null).InsteadOf("R:\\LocalAi").BuildIsRunning);
    }

    /// <summary>
    /// The age is the whole guard against claiming liveness: a build whose process died leaves its
    /// last phase behind, and this is what tells a reader that is what happened. It is therefore
    /// the one figure most often large - nobody clears that record - so it climbs through the
    /// units a reader reads rather than counting minutes to four digits.
    /// </summary>
    [Fact]
    public void The_age_climbs_through_the_units_a_reader_reads()
    {
        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.Seconds("12")),
            Aged(TimeSpan.FromSeconds(12.4)),
            StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.EtaMinutes("38.1")),
            Aged(TimeSpan.FromMinutes(38.1)),
            StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.Hours("3.2")),
            Aged(TimeSpan.FromHours(3.2)),
            StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.Days("2.0")),
            Aged(TimeSpan.FromDays(2)),
            StringComparison.Ordinal);
    }

    private static string Aged(TimeSpan since) =>
        Watching(new SyncSighting(
                Repository,
                BuildHoldsTheGate: true,
                Progress(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511, since)))
            .Above("R:\\LocalAi", SyncNoticeAudience.Answer);

    private static SyncNotice Watching(Func<string?, SyncSighting?> look) => new(look);

    private static SyncNotice Watching(SyncSighting sighting) => new(_ => sighting);

    /// <summary>A sync holding the gate, with figures it has already recorded.</summary>
    private static SyncSighting Running(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total) =>
        new(Repository, BuildHoldsTheGate: true, Progress(phase, processed, total, TimeSpan.Zero));

    /// <summary>
    /// A sync holding the gate whose last write said it is waiting for the backend. Rate zero and
    /// no estimate are not decoration: that is exactly what the build records while it waits, so a
    /// fixture with either of them would be testing a state nothing produces.
    /// </summary>
    private static SyncSighting Paused(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total) =>
        new(
            Repository,
            BuildHoldsTheGate: true,
            Progress(phase, processed, total, TimeSpan.Zero) with
            {
                ChunksPerSecond = 0,
                EstimatedRemaining = null,
                Pause = IndexBuildPause.BackendUnavailable,
            });

    /// <summary>
    /// The pause a killed sync left behind: the field is in the record for ever, like the phase
    /// beside it, and nothing is waiting for anything.
    /// </summary>
    private static SyncSighting StoppedWhilePaused(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total,
        TimeSpan ago) =>
        new(
            Repository,
            BuildHoldsTheGate: false,
            Progress(phase, processed, total, ago) with
            {
                ChunksPerSecond = 0,
                EstimatedRemaining = null,
                Pause = IndexBuildPause.BackendUnavailable,
            });

    /// <summary>A sync that wrote its end and let go of the gate.</summary>
    private static SyncSighting Finished(RepositoryIndexProgressPhase phase) =>
        new(Repository, BuildHoldsTheGate: false, Progress(phase, 1511, 1511, TimeSpan.Zero));

    /// <summary>A record nothing will ever finish, over an open gate: a sync that was killed.</summary>
    private static SyncSighting Stopped(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total,
        TimeSpan ago) =>
        new(Repository, BuildHoldsTheGate: false, Progress(phase, processed, total, ago));

    private static SyncSighting Idle() => new(Repository, BuildHoldsTheGate: false, null);

    private static RepositoryIndexProgress Progress(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total,
        TimeSpan ago) =>
        new(
            Repository,
            phase,
            "R:\\LocalAi",
            processed,
            total,
            10.2,
            TimeSpan.FromMinutes(0.9),
            DateTimeOffset.UtcNow - ago);
}
