using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A publish that loses a race with a reader gave up outright.
///
/// <c>TryReadCurrent</c> opens <c>current.json</c> through <c>File.ReadAllText</c>, which takes
/// <c>FileShare.Read</c> and so denies <c>FILE_SHARE_DELETE</c>. While that handle is open the
/// publish's <c>File.Move(temporary, path, overwrite: true)</c> cannot replace the file and
/// throws — out of <c>SetCurrent</c>, out of the sync, after the whole build.
///
/// <c>current.json</c> has more readers than any other file in the runtime tree: every search,
/// every navigation call, every <c>index_status</c>, every retention pass.
/// <c>BrokerRuntimeStateStore</c> spends a documented budget on exactly this contention for a
/// file with fewer, and these tests are built the way its own are.
/// </summary>
public sealed class APublishWaitsForAReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-publish-contention-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The reader is opened on this thread and released only from another one, and the publish
    /// runs here in between — so its first rename is certain to meet the open handle. The
    /// contention is arranged rather than raced for: nothing here depends on which of two tasks
    /// a scheduler picks first.
    /// </summary>
    [Fact]
    public async Task A_reader_holding_the_pointer_does_not_fail_the_publish()
    {
        var store = Published(out var first, out var second);
        store.SetCurrent(first);
        var reader = File.Open(
            PointerPath,
            FileMode.Open,
            FileAccess.Read,
            // What File.ReadAllText itself takes. FileShare.None would block the rename too, but
            // it would be a lock this product never actually takes on this file.
            FileShare.Read);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(75),
                    TestContext.Current.CancellationToken);
                reader.Dispose();
            },
            TestContext.Current.CancellationToken);

        try
        {
            // A budget far larger than the hold, so what passes is the retry rather than a
            // machine idle enough to finish inside the default. The hold is a floor under how
            // long the obstruction lasts, never a deadline the publish has to beat: a slower
            // machine spends more attempts and still arrives.
            store.SetCurrent(second, TimeSpan.FromSeconds(30));
            await release;
        }
        finally
        {
            reader.Dispose();
        }

        Assert.Equal(second.Identity.Id, store.ReadCurrent()?.GenerationId);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(PointerPath)!, "*.tmp"));
    }

    /// <summary>
    /// And the budget is not infinite: a reader that never lets go still ends in a refusal rather
    /// than a hang, because a publish that cannot happen has to be reported.
    /// </summary>
    [Fact]
    public void A_reader_that_never_lets_go_still_ends_in_a_refusal()
    {
        var store = Published(out var first, out var second);
        store.SetCurrent(first);

        using var reader = File.Open(
            PointerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        // Its own budget, not the shipped one: what is under test is that the budget is finite,
        // and waiting out the real two seconds would spend them proving nothing extra.
        var error = Record.Exception(
            () => store.SetCurrent(second, TimeSpan.FromMilliseconds(50)));

        // Both types, because Windows raises UnauthorizedAccessException for a replace denied by
        // a reader's handle and it does not derive from IOException — which is why the retry
        // filter names both. Asserting one of them would pass on the platform that raises it and
        // hide the other.
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected the publish to be refused, got {error?.GetType().Name ?? "no exception"}.");
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(PointerPath)!, "*.tmp"));
    }

    private string PointerPath => Path.Combine(_root, "repo", "current.json");

    private GenerationStore Published(
        out GenerationManifest first,
        out GenerationManifest second)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        first = store.PublishIndex(source, GenerationStoreTests.Identity());
        second = store.PublishIndex(
            source,
            GenerationStoreTests.Identity() with { RankingVersion = 2 });
        return store;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
