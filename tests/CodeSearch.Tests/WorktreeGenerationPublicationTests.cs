using System.Text.Json;
using CodeSearch.Core.Indexing;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

public sealed class WorktreeGenerationPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "localai-worktree-publication-" + Guid.NewGuid().ToString("N"));
    private string Runtime => Path.Combine(_root, "runtime");
    private GenerationStore Store => new(FsPath.From(Runtime));
    private WorkingIndexIdentity Worktree(string name = "active") => new(
        FsPath.From(Path.Combine(_root, name)), FsPath.From(Path.Combine(_root, "active")),
        "repository", FsPath.From(Runtime), "commit", "tree", null, null);

    [Fact]
    public void Ready_worktree_and_other_worktrees_read_different_complete_generations()
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        WorktreeGenerationPublications.Publish(Worktree(), next, Store.ReadCurrent());

        Assert.Equal(next.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
        Assert.Equal(previous.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree("other"))!.GenerationId);
        Assert.Equal(previous.Identity.Id, Store.ReadCurrent()!.GenerationId);
    }

    [Fact]
    public void A_new_pointer_epoch_invalidates_old_publications_even_if_its_id_is_reused()
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        var pointer = Store.ReadCurrent()!;
        WorktreeGenerationPublications.Publish(Worktree(), next, pointer);
        File.WriteAllText(Path.Combine(Runtime, "current.json"), JsonSerializer.Serialize(
            pointer with { UpdatedAtUtc = pointer.UpdatedAtUtc.AddSeconds(1) }, LocalAiJson.Strict));

        Assert.Equal(previous.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
        Assert.Throws<InvalidOperationException>(() =>
            WorktreeGenerationPublications.Publish(Worktree(), next, pointer));
    }

    [Fact]
    public void Pending_publication_survives_retention_past_the_grace_and_after_restart()
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        WorktreeGenerationPublications.Publish(Worktree(), next, Store.ReadCurrent());

        var result = GenerationRetention.Prune(Runtime,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 }, DateTimeOffset.UtcNow);

        Assert.Empty(result.GenerationsRemoved);
        Assert.Equal(next.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
        Store.SetCurrent(next);
        result = GenerationRetention.Prune(Runtime,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 }, DateTimeOffset.UtcNow);
        Assert.Equal([previous.Identity.Id], result.GenerationsRemoved);
    }

    [Fact]
    public void A_deleted_worktree_does_not_protect_an_unreachable_generation()
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        WorktreeGenerationPublications.Publish(Worktree(), next, Store.ReadCurrent());

        var result = GenerationRetention.Prune(Runtime,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 }, DateTimeOffset.UtcNow,
            reachable: new HashSet<(string, string)>());

        Assert.Equal([next.Identity.Id], result.GenerationsRemoved);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Unreadable_worktree_publication_stops_retention(string content)
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        WorktreeGenerationPublications.Publish(Worktree(), next, Store.ReadCurrent());
        File.WriteAllText(PublicationPath, content);

        var result = GenerationRetention.Prune(Runtime,
            RuntimeRetentionPolicy.Default with { GenerationsPerRepository = 1 }, DateTimeOffset.UtcNow);

        Assert.Empty(result.GenerationsRemoved);
        Assert.Equal(previous.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
    }

    [Fact]
    public void A_path_in_a_generation_id_is_never_used_as_a_path()
    {
        var previous = Publish("old");
        var next = Publish("next");
        Store.SetCurrent(previous);
        WorktreeGenerationPublications.Publish(Worktree(), next, Store.ReadCurrent());
        File.WriteAllText(PublicationPath, File.ReadAllText(PublicationPath)
            .Replace(next.Identity.Id, "../../outside", StringComparison.Ordinal));

        Assert.Equal(previous.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
        Assert.False(WorktreeGenerationPublications.TryGetProtectedGenerations(
            Runtime, Store.ReadCurrent(), null, out _));
    }

    [Fact]
    public void Unreadable_global_pointer_defers_readiness_until_global_repair()
    {
        var next = Publish("next");
        File.WriteAllText(Path.Combine(Runtime, "current.json"), "{broken");

        Assert.False(WorktreeGenerationPublications.Publish(Worktree(), next, expectedCurrent: null));
        Assert.Null(WorktreeGenerationPublications.ReadCurrent(Worktree()));
        Store.SetCurrent(next, expectedCurrent: null);
        Assert.Equal(next.Identity.Id, WorktreeGenerationPublications.ReadCurrent(Worktree())!.GenerationId);
    }

    [Fact]
    public void Reading_an_unconfigured_worktree_creates_nothing()
    {
        Assert.Null(WorktreeGenerationPublications.ReadCurrent(Worktree()));
        Assert.False(Directory.Exists(_root));
    }

    private string PublicationPath => Path.Combine(Runtime, "worktree-publications",
        RuntimeIndexLayout.WorktreeKey(Worktree().WorkingRoot) + ".json");

    private GenerationManifest Publish(string tree)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, tree + ".cidx");
        File.WriteAllText(source, "INDEX");
        return Store.PublishIndex(source, GenerationStoreTests.Identity() with { DevTree = tree },
            publishedAtUtc: DateTimeOffset.UtcNow.AddDays(-10));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
