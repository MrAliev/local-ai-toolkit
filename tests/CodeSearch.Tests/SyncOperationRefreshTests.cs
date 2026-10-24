using CodeSearch.Mcp;
using LocalAi.Contracts;
using LocalAi.Repository;

namespace CodeSearch.Tests;

public sealed class SyncOperationRefreshTests
{
    [Fact]
    public async Task A_cancelled_wait_reports_the_originating_id_and_unknown_backend_state()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        Guid origin = Guid.Empty;
        var result = await CodeSearchTools.RefreshAsync("test-root", "localai.exe", new SyncNotice(_ => null),
            (start, token) =>
            {
                var index = start.ArgumentList.IndexOf("--operation");
                origin = Guid.Parse(start.ArgumentList[index + 1]);
                return Task.FromCanceled<(int, string, string)>(token);
            }, cancelled.Token);
        Assert.NotEqual(Guid.Empty, origin);
        Assert.Contains($"SYNC_WAIT_CANCELLED operation={origin:D} backend_completion=UNKNOWN", result, StringComparison.Ordinal);
        Assert.Contains($"sync cancel --root \"test-root\" --operation {origin:D}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("state=Completed", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_already_running_sync_returns_its_existing_origin_without_starting_another()
    {
        var id = Guid.NewGuid();
        var progress = new RepositoryIndexProgress("repository", RepositoryIndexProgressPhase.EmbeddingOverlay,
            "other-root", 10, 20, 2, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow);
        var operation = new RepositorySyncOperation(id, "repository", "requested-root", "other-root",
            3, 4, 2, "EmbeddingOverlay", "Running", DateTimeOffset.UtcNow);
        var notice = new SyncNotice(_ => new SyncSighting("repository", true, progress, operation));
        var called = false;
        var result = await CodeSearchTools.RefreshAsync("requested-root", "localai.exe", notice,
            (_, _) => { called = true; return Task.FromResult((0, "", "")); }, TestContext.Current.CancellationToken);
        Assert.False(called);
        Assert.Contains($"id={id:D}", result, StringComparison.Ordinal);
        Assert.Contains("pass=3/4 completed_passes=2/4", result, StringComparison.Ordinal);
        Assert.Contains("whole_eta=UNKNOWN", result, StringComparison.Ordinal);
        Assert.Contains("current=\"other-root\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_sync_returns_completion_rather_than_a_timeout_claim()
    {
        var result = await CodeSearchTools.RefreshAsync("test-root", "localai.exe", new SyncNotice(_ => null),
            (_, _) => Task.FromResult((0, "SYNCED repository=test generation=test overlays=0", "")),
            TestContext.Current.CancellationToken);
        Assert.StartsWith("SYNCED ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("backend_completion=UNKNOWN", result, StringComparison.Ordinal);
    }
}
