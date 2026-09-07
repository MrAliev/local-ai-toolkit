using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed class ExperimentStateStore
{
    private const int SchemaVersion = 1;

    public ExperimentStateStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        StatePath = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "experiments",
            "state.json");
    }

    public string StatePath { get; }

    public async Task<ExperimentSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return ExperimentSnapshot.Empty;
        }

        await using var stream = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<ExperimentStateDocument>(
            stream,
            LocalAiJson.Strict,
            cancellationToken)
            ?? throw new InvalidDataException("Experiment state is empty.");
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported experiment state schema '{document.SchemaVersion}'.");
        }

        return ExperimentSnapshot.FromPairs(document.Pairs);
    }

    public async Task SaveAsync(
        ExperimentSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var document = new ExperimentStateDocument(
            SchemaVersion,
            snapshot.Pairs
                .OrderBy(pair => pair.Profile)
                .ThenBy(pair => pair.Model, StringComparer.Ordinal)
                .ToArray());

        // Irrecoverable: what accumulates here is which model answered and which failed, observed
        // once each and derivable from nothing. It was already written through; what changes is
        // that the flush now reaches the disk rather than the operating system's cache, which is
        // what FlushAsync did and what the WriteThrough beside it already implied.
        await AtomicFile.WriteAsync(
            StatePath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, document, LocalAiJson.Strict, token),
            AtomicWrite.Irrecoverable,
            cancellationToken);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ExperimentStateDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] IReadOnlyList<ExperimentPairState> Pairs);
}
