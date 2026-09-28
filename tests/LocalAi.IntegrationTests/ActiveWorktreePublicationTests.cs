using System.Diagnostics;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using CodeSearch.Core.Semantics;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class ActiveWorktreePublicationTests : IDisposable
{
    private readonly string _fixture = Path.Combine(
        Path.GetTempPath(), "localai-active-sync-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_fixture, "repo");
    private string Other => Path.Combine(_fixture, "other");
    private string Runtime => Path.Combine(_fixture, "runtime");

    private sealed record Fixture(string Active, WorkingIndexIdentity Identity,
        GenerationStore Store, GenerationManifest Previous, GenerationManifest Next);

    private Fixture Prepare(bool linkedActive = false)
    {
        Directory.CreateDirectory(Root);
        Git("init", "-b", "main");
        Git("config", "user.email", "tests@local.invalid");
        Git("config", "user.name", "Local AI Tests");
        File.WriteAllText(Path.Combine(Root, "notes.md"), "OldSymbol\n");
        Git("add", ".");
        Git("commit", "-m", "Old source");
        Git("worktree", "add", "-b", "other", Other);
        var oldIdentity = RuntimeIndexLayout.Inspect(Root, Runtime);
        var old = Publish(oldIdentity, "OldSymbol");
        var store = new GenerationStore(oldIdentity.RepositoryRuntimeRoot);
        store.SetCurrent(old);

        File.WriteAllText(Path.Combine(Root, "notes.md"), "ActiveSymbol\n");
        Git("add", ".");
        Git("commit", "-m", "Active source");
        var activeIdentity = RuntimeIndexLayout.Inspect(Root, Runtime);
        var next = Publish(activeIdentity, "ActiveSymbol");
        var active = Root;
        if (linkedActive)
        {
            active = Path.Combine(_fixture, "active");
            Git("worktree", "add", "-b", "active", active);
        }
        return new Fixture(active, RuntimeIndexLayout.Inspect(active, Runtime), store, old, next);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ready_active_tree_survives_failure_and_restart(bool linkedActive)
    {
        var fixture = Prepare(linkedActive);

        // The base is already built, but maintenance cannot save the other tree's semantic
        // overlay. This fails before embedding and touches only this fixture's own runtime.
        var otherIdentity = RuntimeIndexLayout.Inspect(Other, Runtime);
        var blockedPath = RuntimeIndexLayout.SemanticOverlayPath(otherIdentity, fixture.Next.Identity.Id);
        Directory.CreateDirectory(blockedPath);
        var error = await Record.ExceptionAsync(() => CodeSearchSyncCommand.ExecuteAsync(
            fixture.Active, TextWriter.Null, cancellationToken: TestContext.Current.CancellationToken,
            runtimeRoot: Runtime));
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        await AssertReadable(fixture);
        Assert.Equal(fixture.Previous.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);

        Directory.Delete(blockedPath);
        PrepareOtherOverlay(fixture);
        await CodeSearchSyncCommand.ExecuteAsync(fixture.Active, TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken, runtimeRoot: Runtime);
        Assert.Equal(fixture.Next.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture);
    }

    [Fact]
    public async Task Cancellation_after_active_publication_preserves_both_readers_and_can_resume()
    {
        var fixture = Prepare(linkedActive: true);
        PrepareOtherOverlay(fixture);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var output = new OnReadyWriter(cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodeSearchSyncCommand.ExecuteAsync(
            fixture.Active, output, cancellationToken: cancellation.Token, runtimeRoot: Runtime));

        Assert.Equal(fixture.Previous.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture);
        await CodeSearchSyncCommand.ExecuteAsync(fixture.Active, TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken, runtimeRoot: Runtime);
        Assert.Equal(fixture.Next.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_worktree_deleted_before_or_during_maintenance_does_not_block_active(bool during)
    {
        var fixture = Prepare();
        void DeleteOther() => Directory.Delete(Other, recursive: true);
        if (!during) DeleteOther();
        using var output = new OnReadyWriter(() => { if (during) DeleteOther(); });
        await CodeSearchSyncCommand.ExecuteAsync(fixture.Active, output,
            cancellationToken: TestContext.Current.CancellationToken, runtimeRoot: Runtime);
        Assert.Equal(fixture.Next.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture, includeOther: false);
    }

    [Fact]
    public async Task A_live_tree_changed_during_maintenance_preserves_the_previous_global_generation()
    {
        var fixture = Prepare();
        PrepareOtherOverlay(fixture);
        using var output = new OnLineWriter(value =>
        {
            if (value.StartsWith("WORKTREE_PLAN ", StringComparison.Ordinal) &&
                value.Contains(System.Text.Json.JsonSerializer.Serialize(Other), StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(Other, "notes.md"), "Saved while syncing\n");
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeSearchSyncCommand.ExecuteAsync(
            fixture.Active, output, cancellationToken: TestContext.Current.CancellationToken, runtimeRoot: Runtime));
        Assert.Contains("worktree_changed", error.Message);
        Assert.Equal(fixture.Previous.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture, includeOther: false);

        File.WriteAllText(Path.Combine(Other, "notes.md"), "OldSymbol\n");
        await CodeSearchSyncCommand.ExecuteAsync(fixture.Active, TextWriter.Null,
            cancellationToken: TestContext.Current.CancellationToken, runtimeRoot: Runtime);
        Assert.Equal(fixture.Next.Identity.Id, fixture.Store.ReadCurrent()!.GenerationId);
        await AssertReadable(fixture);
    }

    private async Task AssertReadable(Fixture fixture, bool includeOther = true)
    {
        using var search = new SearchService(model => new UnitEmbedding(model), runtimeRoot: Runtime);
        var active = await search.SearchAsync("ActiveSymbol", fixture.Active, new SearchOptions(),
            TestContext.Current.CancellationToken);
        Assert.Equal("ActiveSymbol", Assert.Single(active.Hits).Symbol);
        Assert.Contains("ActiveSymbol", (await search.GetChunkAsync(
            active.Hits[0].ChunkId, fixture.Active, TestContext.Current.CancellationToken)).Body);
        Assert.Equal(fixture.Next.Identity.Id,
            CodeIndex.Load(search.Status(fixture.Active).IndexPath, withVectors: false).GenerationId);
        Assert.Equal(DocumentationWorkspace.Snapshot(fixture.Identity),
            DocumentationWorkspace.Capture(fixture.Active, Runtime,
                cancellationToken: TestContext.Current.CancellationToken).Snapshot);

        if (!includeOther) return;
        var other = await search.SearchAsync("OldSymbol", Other, new SearchOptions(),
            TestContext.Current.CancellationToken);
        Assert.Equal("OldSymbol", Assert.Single(other.Hits).Symbol);
        Assert.Contains("OldSymbol", (await search.GetChunkAsync(
            other.Hits[0].ChunkId, Other, TestContext.Current.CancellationToken)).Body);
    }

    private void PrepareOtherOverlay(Fixture fixture)
    {
        var identity = RuntimeIndexLayout.Inspect(Other, Runtime);
        Index(identity, fixture.Next.Identity.Id, "OldSymbol", fixture.Next.Identity.DevCommit)
            .Save(RuntimeIndexLayout.OverlayPath(identity, fixture.Next.Identity.Id));
        var baseSemantic = SemanticIndex.Load(fixture.Store.SemanticIndexPath(fixture.Next.Identity.Id));
        SemanticIndexOverlay.Create(baseSemantic, baseSemantic with
        {
            GitTree = identity.HeadTree, BaseCommit = identity.HeadCommit
        }, []).Save(RuntimeIndexLayout.SemanticOverlayPath(identity, fixture.Next.Identity.Id));
    }

    private GenerationManifest Publish(WorkingIndexIdentity identity, string symbol)
    {
        var generation = new GenerationIdentity(
            identity.RepositoryId, identity.HeadCommit, identity.HeadTree,
            CodeSearchSyncCommand.DefaultModel, CodeSearchSyncCommand.DefaultDimension,
            CodeSearchSyncCommand.CurrentChunkFormatVersion, CodeIndex.CurrentVersion,
            CodeSearchSyncCommand.CurrentNormalizationVersion, 1,
            CodeSearchSyncCommand.CurrentSemanticGenerationVersion);
        var source = Path.Combine(_fixture, generation.Id + ".cidx");
        var semantic = Path.Combine(_fixture, generation.Id + ".sidx");
        Index(identity, generation.Id, symbol).Save(source);
        new SemanticIndex
        {
            RepositoryId = identity.RepositoryId, GenerationId = generation.Id,
            GitTree = identity.HeadTree, BaseCommit = identity.HeadCommit,
            IndexedAtUtc = DateTime.UtcNow, Documents = [], Symbols = [],
            Occurrences = [], Relationships = []
        }.Save(semantic);
        return new GenerationStore(identity.RepositoryRuntimeRoot).PublishIndex(source, generation, semantic);
    }

    private static CodeIndex Index(WorkingIndexIdentity identity, string generation, string symbol,
        string baseCommit = "")
    {
        var vectors = new float[CodeSearchSyncCommand.DefaultDimension];
        vectors[0] = 1;
        return new CodeIndex
        {
            Dim = vectors.Length, Model = CodeSearchSyncCommand.DefaultModel,
            Root = identity.WorkingRoot.Value, BaseCommit = baseCommit,
            GitCommit = identity.HeadCommit, GitTree = identity.HeadTree,
            RepositoryId = identity.RepositoryId, GenerationId = generation,
            IndexedAtUtc = DateTime.UtcNow,
            Files = [new IndexedFile
            {
                // The persisted corpus contract hashes canonical CRLF text on every OS.
                RelPath = "notes.md", Hash = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(symbol + "\r\n")),
                ChunkStart = 0, ChunkCount = 1
            }],
            Chunks = [new ChunkMeta
            {
                FileIndex = 0, Kind = ChunkKind.Text, Symbol = symbol, Signature = symbol,
                Namespace = "", StartLine = 1, EndLine = 1
            }],
            Vectors = vectors
        };
    }

    private sealed class OnReadyWriter(Action ready) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("ACTIVE_READY ", StringComparison.Ordinal) == true) ready();
        }
    }

    private sealed class OnLineWriter(Action<string> written) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null) written(value);
        }
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_fixture)) return;
        foreach (var path in Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_fixture, recursive: true);
    }

    private sealed class UnitEmbedding(string model) : IEmbeddingClient
    {
        public string Model => model;
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, LocalJobPriority priority,
            string deduplicationKey, CancellationToken cancellationToken = default)
        {
            var vector = new float[CodeSearchSyncCommand.DefaultDimension];
            vector[0] = 1;
            return Task.FromResult<float[][]>([vector]);
        }
    }
}
