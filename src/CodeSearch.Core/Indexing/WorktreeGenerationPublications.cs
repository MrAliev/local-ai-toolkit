using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// A completed worktree may use the next generation while the repository pointer still protects
/// every other worktree's previous generation. The publication belongs to one pointer epoch:
/// once the repository pointer moves, an older publication can never override it.
/// </summary>
public static class WorktreeGenerationPublications
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Publication(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] string WorktreeId,
        [property: JsonRequired] GenerationPointer Generation,
        [property: JsonRequired] GenerationPointer? ExpectedCurrent);

    public static GenerationPointer? ReadCurrent(WorkingIndexIdentity identity)
    {
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        if (!store.TryReadCurrent(out var current)) return null;
        var worktreeId = RuntimeIndexLayout.WorktreeKey(identity.WorkingRoot);
        return TryRead(PathFor(identity.RepositoryRuntimeRoot.Value, worktreeId), out var publication) &&
            publication is not null && publication.WorktreeId == worktreeId &&
            publication.ExpectedCurrent == current
                ? publication.Generation
                : current;
    }

    public static bool Publish(
        WorkingIndexIdentity identity,
        GenerationManifest generation,
        GenerationPointer? expectedCurrent)
    {
        if (identity.RepositoryId != generation.Identity.RepositoryId)
            throw new InvalidOperationException("Worktree and generation belong to different repositories.");
        var store = new GenerationStore(identity.RepositoryRuntimeRoot);
        store.ReadManifest(generation.Identity.Id);
        // An unreadable repository pointer is repaired by the final global publication.
        // Until then no reader can verify this epoch, so do not announce early readiness.
        if (!store.TryReadCurrent(out var current)) return false;
        if (current != expectedCurrent)
            throw new InvalidOperationException("current_pointer_changed: cannot publish a worktree from a stale sync.");
        var worktreeId = RuntimeIndexLayout.WorktreeKey(identity.WorkingRoot);
        AtomicFile.WriteJson(
            PathFor(identity.RepositoryRuntimeRoot.Value, worktreeId),
            new Publication(1, worktreeId, new GenerationPointer(
                generation.Identity.Id, generation.Identity.DevTree, DateTimeOffset.UtcNow), expectedCurrent),
            LocalAiJson.Strict,
            AtomicWrite.Recomputable.Retrying(GenerationStore.PublishRetryBudget));
        return true;
    }

    public static bool TryGetProtectedGenerations(
        string repositoryRuntimeRoot,
        GenerationPointer? current,
        IReadOnlySet<(string WorktreeId, string HeadTree)>? reachable,
        out HashSet<string> generations)
    {
        generations = new HashSet<string>(StringComparer.Ordinal);
        var directory = Path.Combine(repositoryRuntimeRoot, "worktree-publications");
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (!TryRead(path, out var publication) || publication is null ||
                    publication.WorktreeId != Path.GetFileNameWithoutExtension(path)) return false;
                if (publication.ExpectedCurrent == current &&
                    (reachable is null || reachable.Any(item => item.WorktreeId == publication.WorktreeId)))
                    generations.Add(publication.Generation.GenerationId);
            }
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PathFor(string repositoryRuntimeRoot, string worktreeId) =>
        Path.Combine(repositoryRuntimeRoot, "worktree-publications", worktreeId + ".json");

    private static bool TryRead(string path, out Publication? publication)
    {
        publication = null;
        try
        {
            publication = JsonSerializer.Deserialize<Publication>(File.ReadAllText(path), LocalAiJson.Strict);
            return publication is { SchemaVersion: 1, Generation: not null } &&
                GenerationIdentity.IsWellFormedId(publication.WorktreeId) &&
                GenerationIdentity.IsWellFormedId(publication.Generation.GenerationId) &&
                (publication.ExpectedCurrent is null ||
                    GenerationIdentity.IsWellFormedId(publication.ExpectedCurrent.GenerationId));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
