using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// Reusing chunks a previous build already embedded, from the overlay beside the one being
/// written rather than from the one at its own path.
///
/// The overlay's path carries the hash of every dirty file, so it changes on every edit and the
/// destination never exists — the reuse branch could only ever fire on a rebuild where nothing
/// had changed at all, which is the one case with nothing to reuse. The per-file machinery was
/// already there and correct, and had an empty input (#388).
///
/// What makes it sound is per-file: the same bytes and the same chunker. The second is not
/// implied by the first. `ChunkerFactory.Resolve(path, catalog)` promotes a windowed file to a
/// symbol-aware one when the catalogue has definitions for it, so a TypeScript or Python file can
/// keep its bytes and change its boundaries when the semantic index moves underneath it.
/// </summary>
public sealed class OverlayReuseFromANeighbourTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "codesearch-neighbour-" + Guid.NewGuid().ToString("N"));

    public OverlayReuseFromANeighbourTests() => Directory.CreateDirectory(root);

    /// <summary>
    /// The case the issue was filed on: two files diverge from the base, the second build edits
    /// one of them, and the other's vectors are already on disk in the overlay written a moment
    /// ago.
    /// </summary>
    [Fact]
    public async Task A_file_unchanged_since_the_last_overlay_is_not_embedded_again()
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
        Assert.Equal(1, second.FilesEmbedded);
    }

    /// <summary>
    /// And the reuse is exact. A file whose bytes are unchanged but whose chunker has changed —
    /// here the semantic index stopped naming its definitions — is cut again, because its
    /// boundaries came from the catalogue rather than from the file.
    ///
    /// Without this the overlay would carry chunks sliced on boundaries that no longer exist,
    /// with vectors describing text at the old spans: wrong answers rather than stale ones.
    /// </summary>
    [Fact]
    public async Task A_file_whose_chunker_changed_is_cut_again_even_with_the_same_bytes()
    {
        Write("app.ts", Module);
        Write("other.md", "one\ntwo\n");
        var withSymbols = new IndexBuilder(new RecordingEmbedder(), definitions: Catalogue());
        await withSymbols.BuildAsync(root, Base, ct: TestContext.Current.CancellationToken);

        Write("app.ts", Module + "\nexport const tail = 1;\n");
        await withSymbols.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken);

        // `app.ts` is untouched from here on; only the catalogue moves.
        Write("other.md", "one\nedited\n");
        var withoutSymbols = new IndexBuilder(
            new RecordingEmbedder(),
            definitions: SymbolDefinitionCatalog.Empty);
        var second = await withoutSymbols.BuildOverlayAsync(
            root,
            Base,
            Overlay("b"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, second.FilesReused);
        Assert.Equal(2, second.FilesEmbedded);
    }

    /// <summary>
    /// The same file, the same catalogue: reused. Without this the test above would pass on a
    /// build that had simply stopped reusing anything.
    /// </summary>
    [Fact]
    public async Task The_same_catalogue_reuses_the_symbol_aware_file()
    {
        Write("app.ts", Module);
        Write("other.md", "one\ntwo\n");
        var builder = new IndexBuilder(new RecordingEmbedder(), definitions: Catalogue());
        await builder.BuildAsync(root, Base, ct: TestContext.Current.CancellationToken);

        Write("app.ts", Module + "\nexport const tail = 1;\n");
        await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("a"),
            TestContext.Current.CancellationToken);

        Write("other.md", "one\nedited\n");
        var second = await builder.BuildOverlayAsync(
            root,
            Base,
            Overlay("b"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, second.FilesReused);
        Assert.Equal(1, second.FilesEmbedded);
    }

    private const string Module = "export function total(a, b) {\n  return a + b;\n}\n";

    private string Base => Path.Combine(root, "base.cidx");

    /// <summary>
    /// Overlays live in one directory named for the generation, worktree and head tree, and are
    /// told apart only by the dirty hash in the file name — which is what the real layout does.
    /// </summary>
    private string Overlay(string name) => Path.Combine(root, "overlays", name + ".cidx");

    private void Write(string relPath, string text) =>
        File.WriteAllText(Path.Combine(root, relPath), text);

    private static SymbolDefinitionCatalog Catalogue() =>
        SymbolDefinitionCatalog.FromSemanticIndex(new SemanticIndex
        {
            RepositoryId = "repository",
            GenerationId = "generation",
            GitTree = "tree",
            DirtyHash = null,
            BaseCommit = "commit",
            IndexedAtUtc = DateTime.UnixEpoch,
            Documents = [new SemanticDocument { RelPath = "app.ts", Hash = new byte[32] }],
            Symbols = [],
            Occurrences =
            [
                new SemanticOccurrence
                {
                    DocumentPath = "app.ts",
                    SymbolId = "scip-typescript npm app 1.0.0 `app.ts`/total().",
                    Roles = SemanticOccurrenceRoles.Definition,
                    Precision = NavigationPrecision.Precise,
                    Range = new SourceRange(0, 16, 0, 21),
                    EnclosingRange = new SourceRange(0, 0, 2, 1),
                },
            ],
            Relationships = [],
        });

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
