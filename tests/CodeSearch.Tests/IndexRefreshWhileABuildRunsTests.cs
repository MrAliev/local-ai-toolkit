using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// `index_refresh` while another build holds the repository's sync gate. The sync it starts is
/// refused by the gate with exit 75, and the tool reported that as a failed sync although its
/// description promised a refusal naming the build.
/// </summary>
public sealed class IndexRefreshWhileABuildRunsTests
{
    private const string Repository = "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966a";
    private const string Root = @"R:\Repo";
    private const string Executable = @"C:\LocalAi\localai.exe";

    [Fact]
    public async Task A_held_gate_is_named_and_no_sync_is_started()
    {
        var started = 0;

        var answer = await CodeSearchTools.RefreshAsync(
            Root,
            Executable,
            Watching(Running()),
            (_, _) =>
            {
                started++;
                return Exited(75, "localai: busy");
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, started);
        NamesTheRunningBuild(answer);
    }

    /// <summary>
    /// A build can take the gate between the look and the start; the sync then exits 75 against it.
    /// </summary>
    [Fact]
    public async Task A_sync_that_lost_the_gate_after_the_look_names_the_build_that_took_it()
    {
        var sighting = Idle();

        var answer = await CodeSearchTools.RefreshAsync(
            Root,
            Executable,
            new SyncNotice(_ => sighting),
            (_, _) =>
            {
                sighting = Running();
                return Exited(75, "localai: busy");
            },
            TestContext.Current.CancellationToken);

        NamesTheRunningBuild(answer);
    }

    /// <summary>
    /// 75 is also broker_unavailable. With nothing holding the gate it is not a running build.
    /// </summary>
    [Fact]
    public async Task An_exit_75_with_the_gate_free_is_still_a_failure()
    {
        var answer = await CodeSearchTools.RefreshAsync(
            Root,
            Executable,
            Watching(Idle()),
            (_, _) => Exited(75, "localai: broker down"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CodeSearchText.SyncFailed(75, "localai: broker down"), answer);
    }

    /// <summary>
    /// Asking about the gate on every refresh must not spend the end of a build on a caller that
    /// had no use for it: the next answer is owed that notice.
    /// </summary>
    [Fact]
    public async Task A_refresh_does_not_spend_the_end_of_a_build_owed_to_the_next_answer()
    {
        var sighting = Running();
        var notice = new SyncNotice(_ => sighting);
        notice.Above(Root, SyncNoticeAudience.Answer);
        sighting = Finished();

        await CodeSearchTools.RefreshAsync(
            Root,
            Executable,
            notice,
            (_, _) => Exited(0, string.Empty, "SYNCED repository=abc generation=def overlays=1"),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            CodeSearchText.SyncEnded,
            notice.Above(Root, SyncNoticeAudience.Answer),
            StringComparison.Ordinal);
    }

    private static void NamesTheRunningBuild(string answer)
    {
        Assert.Contains(Root, answer, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.NothingHappened, answer, StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.ProgressChunks(3182, 26480, 23298),
            answer,
            StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.BuildAlreadyRunning, answer, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.WhileItRuns, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            CodeSearchText.SyncFailed(75, string.Empty).TrimEnd(),
            answer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.RunInBackground, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("sync --root", answer, StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output, string Error)> Exited(
        int exitCode,
        string error,
        string output = "") =>
        Task.FromResult((exitCode, output, error));

    private static SyncNotice Watching(SyncSighting sighting) => new(_ => sighting);

    private static SyncSighting Idle() => new(Repository, BuildHoldsTheGate: false, null);

    private static SyncSighting Running() =>
        new(Repository, BuildHoldsTheGate: true, Progress(RepositoryIndexProgressPhase.EmbeddingBase));

    private static SyncSighting Finished() =>
        new(Repository, BuildHoldsTheGate: false, Progress(RepositoryIndexProgressPhase.Completed));

    private static RepositoryIndexProgress Progress(RepositoryIndexProgressPhase phase) =>
        new(
            Repository,
            phase,
            Root,
            3182,
            26480,
            10.2,
            TimeSpan.FromMinutes(38.1),
            DateTimeOffset.UtcNow);
}
