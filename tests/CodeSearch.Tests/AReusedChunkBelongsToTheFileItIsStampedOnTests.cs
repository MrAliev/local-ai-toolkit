using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// A file's chunks are kept from a previous index only when they describe that file as it is now.
///
/// `Plan`'s fallback branch keeps the previous entry for any file that produced no fresh chunks,
/// and it stamps the file's *current* hash onto those old chunks and vectors. Three ordinary
/// inputs produce no fresh chunks for a file that did change: content that is only whitespace, a
/// NUL byte from a build tool writing binary through a text extension, and a file locked between
/// the hash and the read.
///
/// The branch was nearly unreachable while an overlay was loaded from its own dirty-hash path —
/// the previous entry's hash then matched by construction. Reusing from a neighbouring overlay
/// made it ordinary, and the damage is self-perpetuating: the next build sees a matching hash and
/// a matching chunker and keeps the wrong chunks again.
/// </summary>
public sealed class AReusedChunkBelongsToTheFileItIsStampedOnTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-stamped-" + Guid.NewGuid().ToString("N"));

    public AReusedChunkBelongsToTheFileItIsStampedOnTests() =>
        Directory.CreateDirectory(root);

    /// <summary>
    /// The failure in the shape a reader would hit it: a file is edited, indexed, then emptied.
    /// Search must not go on answering from the text that was there two edits ago.
    /// </summary>
    [Fact]
    public async Task A_file_that_now_yields_no_chunks_does_not_keep_the_ones_it_had()
    {
        Write("first.md", "the quick brown fox\njumps over the lazy dog\n");
        Write("second.md", "three\nfour\n");
        var builder = new IndexBuilder(new RecordingEmbedder());
        await builder.BuildAsync(root, Base, ct: TestContext.Current.CancellationToken);

        Write("first.md", "the quick brown fox\nchanged the line\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken);

        // Emptied to whitespace, which every chunker declines to cut. The file is still
        // enumerated and still hashed, so it still reaches the plan.
        Write("first.md", "   \n\n   \n");
        Write("second.md", "three\nedited\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("b"),
            TestContext.Current.CancellationToken);

        var overlay = CodeIndex.Load(Overlay("b"));
        Assert.DoesNotContain(
            overlay.Chunks,
            chunk => chunk.LexicalText.Contains("changed the line", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the entry is listed with no chunks, rather than carrying somebody else's.
    ///
    /// This test asserted the opposite when it was written — that the file is not listed at all —
    /// and that expectation was wrong. Dropping the entry is what lets the base go on answering
    /// for the path, because an overlay shadows a base entry only by listing the file or naming
    /// it deleted. Zero chunks says the true thing: this file diverges, and it has nothing to
    /// contribute.
    /// </summary>
    [Fact]
    public async Task A_file_that_now_yields_no_chunks_is_listed_with_none_of_them()
    {
        Write("first.md", "one\ntwo\n");
        Write("second.md", "three\nfour\n");
        var builder = new IndexBuilder(new RecordingEmbedder());
        await builder.BuildAsync(root, Base, ct: TestContext.Current.CancellationToken);

        Write("first.md", "one\nedited\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken);

        Write("first.md", "   \n\n   \n");
        Write("second.md", "three\nedited\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("b"),
            TestContext.Current.CancellationToken);

        var overlay = CodeIndex.Load(Overlay("b"), withVectors: false);
        var entry = Assert.Single(
            overlay.Files,
            file => file.RelPath.EndsWith("first.md", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, entry.ChunkCount);
    }

    /// <summary>
    /// And the base's chunks for that file stop being served.
    ///
    /// The two tests above assert on the overlay alone, which is not what search queries: it
    /// queries base and overlay composed. An overlay shadows a base entry when it lists the file
    /// or names it deleted, and a diverged file that cuts into nothing is in neither list — it
    /// still exists on disk so it is not deleted, and it contributes no chunks so it is not
    /// listed. The base's entry therefore survives, and a search answers with a symbol from a
    /// file that no longer holds it.
    ///
    /// The snippet is withheld — the file hash no longer matches — so what a reader gets is a
    /// phantom: a symbol, a signature and a line range for text that is gone, occupying a result
    /// slot, under a message telling them to run sync and search again. Sync has already run.
    /// </summary>
    [Fact]
    public async Task The_base_stops_answering_for_a_file_that_now_yields_no_chunks()
    {
        Write("first.md", "the quick brown fox\njumps over the lazy dog\n");
        Write("second.md", "three\nfour\n");
        // A commit is named on both, because composing refuses an overlay with no base commit —
        // there is no git repository under a temporary directory to supply one.
        var context = new IndexBuildContext(root, "base-commit", "base-tree", "repo", "generation");
        var builder = new IndexBuilder(new RecordingEmbedder());
        await builder.BuildAsync(
            root,
            Base,
            ct: TestContext.Current.CancellationToken,
            context: context);

        Write("first.md", "   \n\n   \n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken,
            context: context);

        var composed = new CompositeIndex(
            CodeIndex.Load(Base),
            CodeIndex.Load(Overlay("a")));

        ISearchableIndex index = composed;
        var paths = Enumerable.Range(0, index.ChunkCount)
            .Select(index.PathOf)
            .ToArray();
        Assert.DoesNotContain("first.md", paths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control: a file that still cuts into chunks and has not changed since the neighbour was
    /// written is still reused. Without this the two above would pass on a build that had simply
    /// stopped reusing anything.
    /// </summary>
    [Fact]
    public async Task A_file_that_still_matches_is_still_reused()
    {
        Write("first.md", "one\ntwo\n");
        Write("second.md", "three\nfour\n");
        var builder = new IndexBuilder(new RecordingEmbedder());
        await builder.BuildAsync(root, Base, ct: TestContext.Current.CancellationToken);

        Write("first.md", "one\nedited\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken);

        Write("second.md", "three\nedited\n");
        var second = await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("b"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, second.FilesReused);
    }

    private string Base => Path.Combine(root, "base.cidx");

    private string Overlay(string name) => Path.Combine(root, "overlays", name + ".cidx");

    private void Write(string relPath, string text) =>
        File.WriteAllText(Path.Combine(root, relPath), text);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
    }
}
