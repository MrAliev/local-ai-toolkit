using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Repository;

/// <summary>Operation metadata lives beside progress.json so older strict progress readers still work.</summary>
public sealed record RepositorySyncOperation(
    Guid OperationId,
    string RepositoryId,
    string RequestedRoot,
    string CurrentRoot,
    int Pass,
    int TotalPasses,
    int CompletedPasses,
    string Phase,
    string State,
    DateTimeOffset UpdatedAtUtc)
{
    public string Describe() =>
        $"SYNC_OPERATION id={OperationId:D} requested={JsonSerializer.Serialize(RequestedRoot)} " +
        $"current={JsonSerializer.Serialize(CurrentRoot)} pass={(TotalPasses > 0 ? $"{Pass}/{TotalPasses}" : "UNKNOWN")} " +
        $"completed_passes={(TotalPasses > 0 ? $"{CompletedPasses}/{TotalPasses}" : "UNKNOWN")} phase={Phase} recorded_state={State} " +
        "counters=current-pass whole_chunks=UNKNOWN whole_rate=UNKNOWN whole_eta=UNKNOWN " +
        $"cancel=localai sync cancel --root {JsonSerializer.Serialize(RequestedRoot)} --operation {OperationId:D}";
}

public sealed class RepositorySyncOperationStore(FsPath repositoryRuntimeRoot)
{
    private readonly string _path = repositoryRuntimeRoot.Combine("sync-operation.json").Value;

    public RepositorySyncOperation? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<RepositorySyncOperation>(File.ReadAllText(_path), LocalAiJson.Strict);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(RepositorySyncOperation operation) => AtomicFile.WriteJson(
        _path, operation, LocalAiJson.Strict, AtomicWrite.Recomputable);

    /// <summary>A request targets an immutable operation ID, never the next owner of the repository gate.</summary>
    public bool RequestCancellation(Guid operationId)
    {
        if (Read() is not { State: "Running" } current || current.OperationId != operationId)
            return false;
        Directory.CreateDirectory(repositoryRuntimeRoot.Value);
        using var marker = new FileStream(CancellationPath(operationId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
        return true;
    }

    public bool CancellationRequested(Guid operationId) => File.Exists(CancellationPath(operationId));

    public void ClearCancellation(Guid operationId) => File.Delete(CancellationPath(operationId));

    private string CancellationPath(Guid operationId) =>
        repositoryRuntimeRoot.Combine($"sync-cancel-{operationId:N}.request").Value;
}
