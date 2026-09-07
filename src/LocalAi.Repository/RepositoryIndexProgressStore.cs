using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Repository;

public sealed class RepositoryIndexProgressStore
{
    private readonly string _path;

    public RepositoryIndexProgressStore(FsPath repositoryRuntimeRoot)
    {
        _path = repositoryRuntimeRoot.Combine("progress.json").Value;
    }

    /// <summary>
    /// How long the replace keeps trying while a reader holds the file. New here, and the wider
    /// of the two windows #427 was about: <c>Read</c> below opens through
    /// <c>File.ReadAllText</c>, and <c>index_status</c> calls it while a build runs — where the
    /// generation pointer is replaced once per sync, this is replaced on every batch.
    /// </summary>
    private static readonly TimeSpan ReplaceRetryBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Written through and flushed, which on the recoverability axis it need not be: a progress
    /// record is the most recomputable file in the runtime tree — the next report overwrites it
    /// seconds later, and a build that died leaves one nobody acts on. It keeps the flush because
    /// removing durability is a decision to be asked for rather than taken, and that question is
    /// the remainder of #392.
    /// </summary>
    public void Save(RepositoryIndexProgress progress)
    {
        Validate(progress);
        AtomicFile.WriteJson(
            _path,
            new ProgressDocument(1, progress),
            LocalAiJson.Strict,
            AtomicWrite.Irrecoverable.Retrying(ReplaceRetryBudget));
    }

    public RepositoryIndexProgress? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        ProgressDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ProgressDocument>(
                File.ReadAllText(_path),
                LocalAiJson.Strict)
                ?? throw new InvalidDataException("Repository progress is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Repository progress JSON is invalid.", exception);
        }
        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException("Repository progress schema is unsupported.");
        }

        Validate(document.Progress);
        return document.Progress;
    }

    private static void Validate(RepositoryIndexProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(progress.RepositoryId) ||
            string.IsNullOrWhiteSpace(progress.WorkingRoot) ||
            progress.ProcessedChunks < 0 ||
            progress.TotalChunks < 0 ||
            progress.ProcessedChunks > progress.TotalChunks ||
            !double.IsFinite(progress.ChunksPerSecond) ||
            progress.ChunksPerSecond < 0 ||
            progress.EstimatedRemaining < TimeSpan.Zero ||
            progress.UpdatedAtUtc == default)
        {
            throw new InvalidDataException("Repository progress is invalid.");
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ProgressDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] RepositoryIndexProgress Progress);
}
