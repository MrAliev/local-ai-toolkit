using System.Diagnostics;
using CodeSearch.Core.Search;
using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// The answers that name a command, against a repository with no index at all.
///
/// This is the case the whole issue exists for. On a new machine the first build runs for the
/// better part of an hour, and for all of it every search, every navigation call and every status
/// said "no index - build it" and printed a command that would have exited 75 sync_busy against
/// the gate. The reader most likely to follow that instruction is the one who has never seen this
/// product work.
///
/// The composer is tested on its own in SyncNoticeTests; what is asserted here is that these four
/// answers ask it, and that the instruction really does go away and really does come back.
/// </summary>
public sealed class SyncNoticeWiringTests : IDisposable
{
    private const string Repository = "0ecc90199fac80e34b0ad8dfe9daa8bffd7f6f2f5483b82297e7966a";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-wiring-" + Guid.NewGuid().ToString("N"));

    private readonly string _runtimeRoot = Path.Combine(
        Path.GetTempPath(),
        "codesearch-wiring-runtime-" + Guid.NewGuid().ToString("N"));

    public SyncNoticeWiringTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }\r\n");
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "LocalAi Tests");
        Git("add", ".");
        Git("commit", "-m", "Initial");
    }

    [Fact]
    public async Task A_search_with_no_index_is_not_told_to_start_the_build_that_is_running()
    {
        var answer = await CodeSearchTools.SearchCode(
            Service(),
            Watching(Running()),
            "anything at all",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.BuildItBackground, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("sync --root", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the command comes back the moment nothing is running - which is the whole reason the
    /// gate is asked rather than the record. A killed sync leaves its phase behind for ever, so a
    /// notice that read the record alone would withhold this command until somebody noticed.
    /// </summary>
    [Fact]
    public async Task A_search_with_no_index_and_a_build_that_died_gets_the_command_back()
    {
        var answer = await CodeSearchTools.SearchCode(
            Service(),
            Watching(Died()),
            "anything at all",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(CodeSearchText.BuildItBackground, answer, StringComparison.Ordinal);
        Assert.Contains("sync --root", answer, StringComparison.Ordinal);
        Assert.Contains(
            CodeSearchText.ProgressChunks(3182, 26480, 23298),
            answer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ETA", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_with_no_index_and_nothing_running_is_unchanged()
    {
        var answer = await CodeSearchTools.SearchCode(
            Service(),
            NoBuildRunning.Notice,
            "anything at all",
            _root,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(CodeSearchText.BuildItBackground, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync:", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_with_no_index_is_not_told_to_start_the_build_that_is_running()
    {
        var status = CodeSearchTools.IndexStatus(Service(), Watching(Running()), _root);

        Assert.Contains(CodeSearchText.BuildAlreadyRunning, status, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.BuildItWith, status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_with_no_index_and_nothing_running_still_says_how_to_build_one()
    {
        var status = CodeSearchTools.IndexStatus(Service(), NoBuildRunning.Notice, _root);

        Assert.Contains(CodeSearchText.BuildItWith, status, StringComparison.Ordinal);
        Assert.Contains("sync --root", status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal already says "not inside a tool call, run this instead". While a build holds the
    /// gate that instruction is the one command that cannot run, so it is replaced in place.
    /// </summary>
    [Fact]
    public void A_refused_refresh_does_not_offer_a_command_the_gate_would_refuse()
    {
        var refusal = CodeSearchTools.RefusalMessage(
            _root,
            "REFUSED repository=abc files=612 limit=200 overlays=0",
            "localai sync --root " + _root,
            limit: 200,
            notice: Watching(Running()));

        Assert.NotNull(refusal);
        Assert.Contains(CodeSearchText.BuildAlreadyRunning, refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(CodeSearchText.RunInBackground, refusal, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.TooBigForToolCall, refusal, StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.WhileItRuns, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_refresh_with_nothing_running_still_names_the_command()
    {
        var refusal = CodeSearchTools.RefusalMessage(
            _root,
            "REFUSED repository=abc files=612 limit=200 overlays=0",
            "localai sync --root " + _root,
            limit: 200,
            notice: NoBuildRunning.Notice);

        Assert.NotNull(refusal);
        Assert.Contains(CodeSearchText.RunInBackground, refusal, StringComparison.Ordinal);
        Assert.Contains("localai sync --root", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chunk id is bound to the generation and tree it was printed from, so a build that
    /// published mid-session is exactly what makes one stop working. The refusal is where that
    /// reader asks why, and it is the best place in the product for the notice that the build has
    /// finished.
    /// </summary>
    [Fact]
    public async Task A_refused_chunk_id_says_what_the_build_behind_it_is_doing()
    {
        var notice = Watching(Running());
        var answer = await CodeSearchTools.GetCodeChunk(
            Service(),
            notice,
            "not-a-chunk-id",
            _root,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            CodeSearchText.ProgressChunks(3182, 26480, 23298),
            answer,
            StringComparison.Ordinal);
        Assert.Contains(CodeSearchText.SyncNotSearchableYet, answer, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Delete(_root);
        Delete(_runtimeRoot);
    }

    private SearchService Service() => new(runtimeRoot: _runtimeRoot);

    private static SyncNotice Watching(SyncSighting sighting) => new(_ => sighting);

    private static SyncSighting Running() =>
        new(Repository, BuildHoldsTheGate: true, Progress(DateTimeOffset.UtcNow));

    private static SyncSighting Died() =>
        new(
            Repository,
            BuildHoldsTheGate: false,
            Progress(DateTimeOffset.UtcNow - TimeSpan.FromHours(3.2)));

    private static RepositoryIndexProgress Progress(DateTimeOffset updatedAtUtc) =>
        new(
            Repository,
            RepositoryIndexProgressPhase.EmbeddingBase,
            "R:\\IntelWash",
            3182,
            26480,
            10.2,
            TimeSpan.FromMinutes(38.1),
            updatedAtUtc);

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
    }

    /// <summary>
    /// Best effort. Git marks the files under .git/objects read-only, so a delete of a real
    /// repository can fail on Windows - and a temporary directory left behind must not be reported
    /// as a failure of the thing under test.
    /// </summary>
    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
