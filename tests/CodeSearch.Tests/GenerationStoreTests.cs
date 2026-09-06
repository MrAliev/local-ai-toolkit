using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class GenerationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-generation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Identity_includes_tree_model_dimension_and_format_versions()
    {
        var identity = Identity();
        var changed = identity with { RankingVersion = 2 };
        var previousNormalization = identity with { NormalizationVersion = 3 };
        var canonicalCrlf = previousNormalization with { NormalizationVersion = 4 };
        var semantic = identity with { SemanticIndexVersion = 1 };

        Assert.NotEqual(identity.Id, changed.Id);
        Assert.NotEqual(previousNormalization.Id, canonicalCrlf.Id);
        Assert.NotEqual(identity.Id, semantic.Id);
        Assert.True(semantic.CanReuseCorpusFrom(identity));
        Assert.Equal(64, identity.Id.Length);
    }

    [Fact]
    public void Corpus_reuse_requires_the_same_normalization_and_indexing_contract()
    {
        var current = Identity() with { NormalizationVersion = 4 };

        Assert.False(current.CanReuseCorpusFrom(
            current with { NormalizationVersion = 3 }));
        Assert.False(current.CanReuseCorpusFrom(
            current with { ChunkFormatVersion = current.ChunkFormatVersion + 1 }));
        Assert.False(current.CanReuseCorpusFrom(
            current with { EmbeddingModel = "other-model" }));
        Assert.True(current.CanReuseCorpusFrom(
            current with
            {
                DevCommit = "next-commit",
                DevTree = "next-tree",
                RankingVersion = current.RankingVersion + 1
            }));
    }

    [Fact]
    public void Read_only_store_construction_does_not_create_runtime_directories()
    {
        var path = Path.Combine(_root, "not-configured");

        var current = new GenerationStore(FsPath.From(path)).ReadCurrent();

        Assert.Null(current);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Published_generation_is_immutable_and_checksum_validated()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        var manifest = store.PublishIndex(source, Identity());

        Assert.Equal(manifest, store.ReadManifest(manifest.Identity.Id));
        File.AppendAllText(store.IndexPath(manifest.Identity.Id), "TAMPERED");
        Assert.Throws<InvalidDataException>(
            () => store.ReadManifest(manifest.Identity.Id));
    }

    [Fact]
    public void Publishes_and_validates_an_optional_semantic_index()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        var semantic = Path.Combine(_root, "source.sidx");
        File.WriteAllText(source, "INDEX");
        File.WriteAllText(semantic, "SEMANTIC");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));

        var manifest = store.PublishIndex(
            source,
            Identity() with { SemanticIndexVersion = 1 },
            sourceSemanticIndexPath: semantic,
            semanticAdapterStatuses:
            [
                new SemanticAdapterStatus(
                    "typescript",
                    SemanticAdapterState.Skipped,
                    "not installed",
                    12)
            ]);

        Assert.Equal("semantic.sidx", manifest.SemanticIndexFile);
        Assert.NotNull(manifest.SemanticIndexChecksum);
        Assert.Equal(
            SemanticAdapterState.Skipped,
            Assert.Single(store.ReadManifest(manifest.Identity.Id).SemanticAdapters!).State);
        Assert.Equal("SEMANTIC", File.ReadAllText(store.SemanticIndexPath(manifest.Identity.Id)));

        File.AppendAllText(store.SemanticIndexPath(manifest.Identity.Id), "TAMPERED");
        Assert.Throws<InvalidDataException>(() => store.ReadManifest(manifest.Identity.Id));
    }

    [Fact]
    public void Does_not_mutate_an_existing_generation_to_attach_semantic_data()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        var semantic = Path.Combine(_root, "source.sidx");
        File.WriteAllText(source, "INDEX");
        File.WriteAllText(semantic, "SEMANTIC");
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));
        var identity = Identity();
        store.PublishIndex(source, identity);

        Assert.Throws<InvalidOperationException>(
            () => store.PublishIndex(source, identity, sourceSemanticIndexPath: semantic));
    }

    /// <summary>
    /// A generation is immutable, so a damaged one is not repaired — it is rebuilt. The obstacle
    /// is that <see cref="GenerationStore.PublishIndex"/> returns the existing manifest whenever
    /// the directory is there, which means reading the damaged one again and failing the same
    /// way. The rebuild cannot begin until the name is free (#390).
    ///
    /// Moved rather than deleted, so whatever went wrong can still be looked at, and into
    /// <c>staging</c>, where retention already collects what no build could still be holding.
    /// </summary>
    [Fact]
    public void An_unreadable_generation_moves_aside_so_the_rebuild_can_take_its_name()
    {
        Directory.CreateDirectory(_root);
        var runtimeRoot = Path.Combine(_root, "repo");
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(runtimeRoot));
        var identity = Identity();
        store.PublishIndex(source, identity);
        File.WriteAllText(store.IndexPath(identity.Id), "TRUNCATED");
        Assert.Throws<InvalidDataException>(() => store.ReadManifest(identity.Id));

        Assert.True(store.QuarantineUnreadable(identity.Id));

        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "generations", identity.Id)));
        var quarantined = Assert.Single(Directory.GetDirectories(
            Path.Combine(runtimeRoot, "staging"),
            identity.Id + ".corrupt-*"));
        Assert.Equal("TRUNCATED", File.ReadAllText(Path.Combine(quarantined, "base.cidx")));

        // The point of the whole exercise: the name is free, so the rebuild publishes into it —
        // a new manifest with its own publication time, and an index that reads.
        var rebuilt = store.PublishIndex(source, identity);
        Assert.Equal(identity.Id, rebuilt.Identity.Id);
        Assert.Equal(rebuilt, store.ReadManifest(identity.Id));
    }

    [Fact]
    public void A_generation_that_is_not_there_is_not_quarantined()
    {
        var store = new GenerationStore(FsPath.From(Path.Combine(_root, "repo")));

        Assert.False(store.QuarantineUnreadable(Identity().Id));
    }

    /// <summary>
    /// A document whose members are absent is not a pointer. Without required members the
    /// deserializer fills the constructor parameters with defaults, so `{}` parses into a
    /// <see cref="GenerationPointer"/> whose <c>GenerationId</c> is null — which reads as a valid
    /// pointer and is then handed to <c>Path.Combine</c> and <c>ReadManifest</c>, neither of which
    /// is guarded against null.
    ///
    /// Every other document this runtime writes already says so: <c>ProgressDocument</c> and
    /// <c>JobStateDocument</c> mark every member required, including the nullable ones. This was
    /// the exception, and it is the file whose loss costs the most.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"DevTree\":\"tree\",\"UpdatedAtUtc\":\"2026-09-06T00:00:00+00:00\"}")]
    public void A_pointer_missing_its_generation_id_is_not_a_pointer(string content)
    {
        var runtimeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(runtimeRoot, "current.json"), content);
        var store = new GenerationStore(FsPath.From(runtimeRoot));

        Assert.False(store.TryReadCurrent(out var pointer));
        Assert.Null(pointer);
    }

    /// <summary>
    /// A pointer that cannot be read is not a different kind of answer from a pointer that is not
    /// there: in both cases nothing is known to be current. It used to be the difference between
    /// an empty repository and a broken installation — `File.Exists` said yes, `Deserialize` threw
    /// `JsonException`, and it escaped through every caller.
    ///
    /// Seven of the eight call sites had no guard, and two of those are on the read path —
    /// `RuntimeIndexLayout` and `SemanticNavigationGateway` — so a `current.json` that does not
    /// parse took search and navigation down with the sync. Guarding seven callers is the wrong
    /// shape; the swallow belongs here, once.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("{\"GenerationId\":42}")]
    public void A_pointer_that_cannot_be_read_reads_as_no_pointer(string content)
    {
        var runtimeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(runtimeRoot, "current.json"), content);
        var store = new GenerationStore(FsPath.From(runtimeRoot));

        Assert.Null(store.ReadCurrent());
    }

    /// <summary>
    /// And the distinction that collapse would otherwise destroy, kept for the one caller that
    /// must not read the two alike.
    ///
    /// <c>GenerationRetention</c> prunes on the strength of what is current: with nothing current
    /// nothing has been superseded and a pass may prune down to the count, but an unreadable
    /// pointer may be naming the very generation the pass is about to remove — and that is an
    /// hour of embedding, not a lookup. So it asks the question that tells them apart, and
    /// everything else on the read path goes on asking the one that does not.
    /// </summary>
    [Fact]
    public void A_pointer_that_is_absent_and_one_that_is_unreadable_are_told_apart()
    {
        var runtimeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(runtimeRoot));

        Assert.True(store.TryReadCurrent(out var absent));
        Assert.Null(absent);

        var manifest = store.PublishIndex(source, Identity());
        store.SetCurrent(manifest);

        Assert.True(store.TryReadCurrent(out var named));
        Assert.Equal(manifest.Identity.Id, named?.GenerationId);

        File.WriteAllText(Path.Combine(runtimeRoot, "current.json"), "{not json");

        Assert.False(store.TryReadCurrent(out var unreadable));
        Assert.Null(unreadable);
    }

    /// <summary>
    /// And the recovery that follows from it, stated deliberately rather than arrived at.
    ///
    /// `SetCurrent(generation, expectedCurrent)` compares what it observes now against what the
    /// caller observed when planning (#199, so an older run cannot move the index backwards over
    /// a newer one). With an unreadable pointer both reads answer null, the comparison matches,
    /// and the publish goes through. That is the intended outcome: a pointer nobody can read
    /// names no generation to protect, and publishing over it is how the repository comes back.
    /// </summary>
    [Fact]
    public void An_unreadable_pointer_does_not_block_the_publish_that_repairs_it()
    {
        var runtimeRoot = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.cidx");
        File.WriteAllText(source, "INDEX");
        var store = new GenerationStore(FsPath.From(runtimeRoot));
        var manifest = store.PublishIndex(source, Identity());
        File.WriteAllText(Path.Combine(runtimeRoot, "current.json"), string.Empty);

        store.SetCurrent(manifest, expectedCurrent: store.ReadCurrent());

        Assert.Equal(manifest.Identity.Id, store.ReadCurrent()?.GenerationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static GenerationIdentity Identity() => new(
        "repository",
        "commit",
        "tree",
        "qwen3-embedding:8b-q8_0",
        4096,
        1,
        CodeIndex.CurrentVersion,
        1,
        1);
}
