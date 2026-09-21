using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>Backend-reported nanoseconds, never inferred from broker wall-clock time.</summary>
public sealed record EmbeddingBackendTiming(
    bool CacheHit,
    long? TotalNanoseconds,
    long? LoadNanoseconds,
    long? PromptEvalNanoseconds,
    long? PromptTokens);

public sealed record EmbeddingTimingRecord(
    int SchemaVersion,
    Guid JobId,
    DateTimeOffset RecordedAtUtc,
    EmbeddingBackendTiming Backend);

/// <summary>
/// Opt-in, content-free diagnostics, separate from the public job response contract.
/// Readers join a record with its existing broker receipt by JobId.
/// </summary>
public sealed class EmbeddingTimingStore(string runtimeRoot)
{
    public const string EnableEnvironmentVariable = "LOCALAI_EMBEDDING_TIMINGS";
    private readonly string _directory = Path.Combine(Path.GetFullPath(runtimeRoot), "telemetry", "embedding-timings");

    public void Write(Guid jobId, EmbeddingBackendTiming timing)
    {
        var record = new EmbeddingTimingRecord(1, jobId, DateTimeOffset.UtcNow, timing);
        Validate(record, jobId);
        AtomicFile.WriteJson(PathFor(jobId), record, LocalAiJson.Strict, AtomicWrite.Irrecoverable);
    }

    public EmbeddingTimingRecord? Read(Guid jobId)
    {
        string json;
        try { json = File.ReadAllText(PathFor(jobId)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var record = JsonSerializer.Deserialize<EmbeddingTimingRecord>(json, LocalAiJson.Strict);
        Validate(record, jobId);
        return record;
    }

    public string PathFor(Guid jobId) => Path.Combine(_directory, $"{jobId:N}.json");

    private static void Validate(EmbeddingTimingRecord? record, Guid jobId)
    {
        if (record is not { SchemaVersion: 1, Backend: { } timing } || jobId == Guid.Empty || record.JobId != jobId ||
            timing.TotalNanoseconds < 0 || timing.LoadNanoseconds < 0 || timing.PromptEvalNanoseconds < 0 || timing.PromptTokens < 0 ||
            (timing.TotalNanoseconds is { } total && (timing.LoadNanoseconds > total ||
                timing.PromptEvalNanoseconds > total - (timing.LoadNanoseconds ?? 0))) ||
            (timing.CacheHit && (timing.TotalNanoseconds is not null || timing.LoadNanoseconds is not null ||
                timing.PromptEvalNanoseconds is not null || timing.PromptTokens is not null)))
            throw new InvalidDataException("Invalid embedding timing record.");
    }
}
