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
    /// seconds later, and a build that died leaves one nobody acts on.
    ///
    /// It keeps the flush anyway, and this is the decision #392 asked for rather than an omission.
    /// What the axis answers is whether losing the file costs a fact or a recomputation, and here
    /// it costs neither: the file is not lost, it is stale, and a stale progress record is read by
    /// <c>SyncNotice</c> as a build that stopped. The flush is what keeps the last thing a build
    /// said about itself on the disk when the machine goes down mid-sync, so the next reader is
    /// told where it stopped rather than where it was some seconds earlier. That is worth one
    /// flush per embedding batch — a cost paid once every fifteen seconds against a write the
    /// operating system was going to make anyway.
    ///
    /// The pointer beside it is the opposite case and says so in <c>GenerationStore</c>: nothing
    /// reads a stale pointer as a statement about a run in progress.
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

    /// <summary>
    /// Takes the record away, for a run that decided not to be one. Absent reads as "no build",
    /// which is the truth after a refusal; a record left in whatever phase it reached reads as a
    /// build that stopped.
    /// </summary>
    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A record nobody can delete is one the next build overwrites anyway.
        }
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
            // Held to what the chunk pair beside them is held to, because both are printed as a
            // fraction: `analysed 30/26 projects` is a sentence a reader can do nothing with, and
            // a renderer should not have to defend itself against its own record. Zero of zero is
            // valid and is the common case — most steps have no figure at all.
            progress.StepDone < 0 ||
            progress.StepTotal < 0 ||
            progress.StepDone > progress.StepTotal ||
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
