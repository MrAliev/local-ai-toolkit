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
/// Asserted against the catalogue rather than against English words: these lines are printed in
/// the reader's language and the tests have to hold in both.
/// </summary>
public sealed class SyncNoticeTests
{
    private const string Repository = "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966a";

    [Fact]
    public void A_counting_phase_says_the_phase_the_chunks_and_how_long_ago()
    {
        var notice = Watching(Building(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

        var above = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.StartsWith("Sync: EmbeddingBase - ", above, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.ProgressChunks(948, 1511, 563), above, StringComparison.Ordinal);
        Assert.Contains("ETA ", above, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.SyncNotSearchableYet, above, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", above, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence explains; the figures report. Repeating the sentence on every call of a build
    /// that lasts minutes would train a reader to skip the whole line, figures included.
    /// </summary>
    [Fact]
    public void The_sentence_is_said_once_and_the_figures_every_time()
    {
        var notice = Watching(Building(RepositoryIndexProgressPhase.EmbeddingBase, 948, 1511));

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
        var notice = Watching(Building(RepositoryIndexProgressPhase.EmbeddingBase, 12, 26480));

        var above = notice.Above("R:\\IntelWash", SyncNoticeAudience.Instruction);

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, above, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.SyncNotSearchableYet, above, StringComparison.Ordinal);
    }

    /// <summary>
    /// A phase that counts nothing says so. Printing the previous phase's finished tally beside it
    /// is what made a build with minutes left read as one that had finished and stalled.
    /// </summary>
    [Fact]
    public void A_phase_that_counts_nothing_says_so_rather_than_showing_a_stale_tally()
    {
        var notice = Watching(Building(RepositoryIndexProgressPhase.SemanticBase, 1511, 1511));

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
        var phase = RepositoryIndexProgressPhase.EmbeddingBase;
        var notice = Watching(_ => Building(phase, 948, 1511)(null));
        notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        phase = RepositoryIndexProgressPhase.Completed;
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
        var notice = Watching(
            Building(RepositoryIndexProgressPhase.Completed, 1511, 1511));

        Assert.Equal(string.Empty, notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer));
    }

    [Fact]
    public void Nothing_recorded_says_nothing()
    {
        var notice = new SyncNotice(_ => null);

        Assert.Equal(string.Empty, notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer));
    }

    /// <summary>
    /// The age is the whole guard against claiming liveness: a build whose process died leaves its
    /// last phase behind, and this is what tells a reader that is what happened.
    /// </summary>
    [Fact]
    public void A_build_that_stopped_moving_shows_its_age_in_minutes()
    {
        var notice = Watching(_ => new RepositoryIndexProgress(
            Repository,
            RepositoryIndexProgressPhase.EmbeddingBase,
            "R:\\LocalAi",
            948,
            1511,
            10.2,
            TimeSpan.FromMinutes(0.9),
            DateTimeOffset.UtcNow - TimeSpan.FromHours(3.2)));

        var above = notice.Above("R:\\LocalAi", SyncNoticeAudience.Answer);

        Assert.Contains(
            CodeSearchText.RecordedAgo(CodeSearchText.EtaMinutes("192.0")),
            above,
            StringComparison.Ordinal);
    }

    private static SyncNotice Watching(Func<string?, RepositoryIndexProgress?> read) =>
        new(read);

    private static Func<string?, RepositoryIndexProgress?> Building(
        RepositoryIndexProgressPhase phase,
        int processed,
        int total) =>
        _ => new RepositoryIndexProgress(
            Repository,
            phase,
            "R:\\LocalAi",
            processed,
            total,
            10.2,
            TimeSpan.FromMinutes(0.9),
            DateTimeOffset.UtcNow);
}
