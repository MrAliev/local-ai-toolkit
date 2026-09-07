using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// The vector block of a finished index is one array — <c>new float[(long)totalChunks * dim]</c> —
/// and an array holds at most <c>Array.MaxLength</c> elements, which at 4096 dimensions is about
/// 524 000 chunks. That allocation is the last step of a build that may have run for hours, so a
/// repository over the ceiling paid for every embedding before finding out (#397).
///
/// Nothing here builds 524 000 chunks. The ceiling is a product of two numbers, so these reach it
/// by the width instead of by the count: at a dimension of 2^30 the ceiling is one chunk, against
/// the real <c>Array.MaxLength</c> and through the real entry point. Two chunks are refused, one
/// is built — the boundary itself, for a millisecond rather than eight gigabytes.
/// </summary>
public sealed class ABuildRefusesAVectorBlockItCannotAllocateTests : IDisposable
{
    /// <summary>
    /// Chosen so that the ceiling is exactly one chunk: 2^30 floats fit inside
    /// <c>Array.MaxLength</c> (2 147 483 591), and two of them are 2 147 483 648 — fifty-seven
    /// elements past what any array can hold, on any machine.
    /// </summary>
    private const int Dimension = 1 << 30;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-vector-block-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Chunks_that_cannot_share_one_array_are_refused_before_anything_is_embedded()
    {
        var sourceRoot = SourceTree(files: 2);
        var embedder = new CountingEmbedder();
        var indexPath = Path.Combine(_root, "index.cidx");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IndexBuilder(embedder).BuildAsync(
                sourceRoot,
                indexPath,
                ct: TestContext.Current.CancellationToken,
                expectedEmbeddingDimension: Dimension));

        // The whole point of the change: refused before the hours, not after them.
        Assert.Equal(0, embedder.Calls);
        Assert.False(File.Exists(indexPath));
        // Both figures, and the ceiling as the quotient it is rather than a constant written
        // down beside it.
        Assert.Contains("2 chunks", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("at most 1 at 1073741824 dimensions", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it reaches a Russian reader in Russian, like every other sentence this assembly
    /// refuses with — the figures untouched, because they are quoted and parsed elsewhere.
    /// </summary>
    [Fact]
    public async Task The_refusal_follows_the_reader()
    {
        using var reading = TestCulture.Reading("ru");
        var sourceRoot = SourceTree(files: 2);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IndexBuilder(new CountingEmbedder()).BuildAsync(
                sourceRoot,
                Path.Combine(_root, "index.cidx"),
                ct: TestContext.Current.CancellationToken,
                expectedEmbeddingDimension: Dimension));

        Assert.Contains("чанков 2", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("не больше 1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_largest_block_that_still_fits_is_built()
    {
        var sourceRoot = SourceTree(files: 1);
        var embedder = new CountingEmbedder();
        var indexPath = Path.Combine(_root, "index.cidx");

        await new IndexBuilder(embedder).BuildAsync(
            sourceRoot,
            indexPath,
            ct: TestContext.Current.CancellationToken,
            expectedEmbeddingDimension: Dimension);

        Assert.True(File.Exists(indexPath));
        Assert.Equal(1, embedder.Calls);
    }

    /// <summary>
    /// An overlay assembles its vectors through the same array, so it meets the same ceiling and
    /// is refused at the same place — before the embedding rather than after it.
    /// </summary>
    [Fact]
    public async Task An_overlay_is_refused_by_the_same_ceiling()
    {
        var sourceRoot = SourceTree(files: 2);
        var embedder = new CountingEmbedder();
        var basePath = Path.Combine(_root, "base.cidx");

        await new IndexBuilder(embedder).BuildAsync(
            sourceRoot,
            basePath,
            ct: TestContext.Current.CancellationToken);

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.md"))
        {
            File.AppendAllText(path, "\nedited\n");
        }

        var embeddedByTheBase = embedder.Calls;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IndexBuilder(embedder).BuildOverlayAsync(
                sourceRoot,
                basePath,
                Path.Combine(_root, "overlay.cidx"),
                ct: TestContext.Current.CancellationToken,
                expectedEmbeddingDimension: Dimension));

        Assert.Equal(embeddedByTheBase, embedder.Calls);
    }

    /// <summary>
    /// One chunk each, and distinct, so the count under test is the file count and nothing about
    /// the chunker has to be assumed.
    /// </summary>
    private string SourceTree(int files)
    {
        var sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceRoot);
        for (var index = 0; index < files; index++)
        {
            File.WriteAllText(
                Path.Combine(sourceRoot, $"notes-{index:D2}.md"),
                $"unique-{index:D2}\n" + new string((char)('a' + index), 2_000));
        }

        return sourceRoot;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CountingEmbedder : IEmbeddingClient
    {
        public string Model => "test-model";

        public int Calls { get; private set; }

        public Task<float[][]> EmbedAsync(
            IReadOnlyList<string> inputs,
            LocalJobPriority priority,
            string deduplicationKey,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(inputs.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
