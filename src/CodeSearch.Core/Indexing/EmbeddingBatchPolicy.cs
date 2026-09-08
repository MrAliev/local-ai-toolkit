using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace CodeSearch.Core.Indexing;

/// <summary>
/// The last embedding batch size proved stable on this machine, per model. The file lives under
/// machine-local settings because moving a learned answer to a machine with another GPU, shared
/// memory or a processor-only backend would turn it into a guess.
/// </summary>
public sealed class EmbeddingBatchProfileStore
{
    private const int SchemaVersion = 1;
    private const string FileName = "embedding-batches.json";
    private static readonly TimeSpan WriteRetryBudget = TimeSpan.FromSeconds(2);

    private readonly string _mutexName;

    public EmbeddingBatchProfileStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        StatePath = RuntimeDirectories.SettingsFileForWriting(
            Path.GetFullPath(runtimeRoot),
            FileName);
        var mutexKey = OperatingSystem.IsWindows() ? StatePath.ToUpperInvariant() : StatePath;
        _mutexName = "LocalAi.EmbeddingBatchProfiles." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(mutexKey)));
    }

    public string StatePath { get; }

    internal int Read(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        try
        {
            using var lease = EnterMutex();
            var profiles = ReadProfiles();
            return profiles.TryGetValue(model, out var saved)
                ? AdaptiveEmbeddingBatchPolicy.Normalize(saved)
                : AdaptiveEmbeddingBatchPolicy.MinimumItems;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return AdaptiveEmbeddingBatchPolicy.MinimumItems;
        }
    }

    internal bool TryWrite(string model, int stableItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        stableItems = AdaptiveEmbeddingBatchPolicy.Normalize(stableItems);
        try
        {
            using var lease = EnterMutex();
            var profiles = ReadProfiles();
            profiles[model] = stableItems;
            AtomicFile.WriteJson(
                StatePath,
                new ProfileDocument(SchemaVersion, profiles),
                LocalAiJson.Strict,
                AtomicWrite.Recomputable.Retrying(WriteRetryBudget));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                TimeoutException)
        {
            // Learning is an optimisation. A read-only or temporarily locked settings directory
            // must not stop a build whose embedding backend is healthy.
            return false;
        }
    }

    private Dictionary<string, int> ReadProfiles()
    {
        if (!File.Exists(StatePath))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(
                File.ReadAllText(StatePath),
                LocalAiJson.Strict);
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                return new Dictionary<string, int>(StringComparer.Ordinal);
            }

            return new Dictionary<string, int>(document.StableItemsByModel, StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private IDisposable EnterMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            try
            {
                if (!mutex.WaitOne(WriteRetryBudget))
                {
                    throw new TimeoutException(
                        "Timed out waiting for the embedding batch profile lock.");
                }
            }
            catch (AbandonedMutexException)
            {
                // Ownership was granted while reporting the previous writer's exit.
            }

            return new MutexLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ProfileDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Dictionary<string, int> StableItemsByModel);

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}

internal sealed record EmbeddingBatchGrowth(int StableItems, int NextItems);

/// <summary>
/// Learns the item-count limit independently from the character budget. Character throughput
/// follows GPU, integrated-memory and CPU speed; this limit bounds Ollama's per-input concurrent
/// tokenisation and the sockets it creates before the runner's embedding semaphore.
/// </summary>
internal sealed class AdaptiveEmbeddingBatchPolicy
{
    public const int MinimumItems = 4;
    public const int MaximumItems = 256;
    public const int SuccessesBeforeGrowth = 3;

    private readonly object _gate = new();
    private readonly string _model;
    private readonly EmbeddingBatchProfileStore? _store;
    private int _stableItems;
    private int _currentItems;
    private int _consecutiveFullSuccesses;

    public AdaptiveEmbeddingBatchPolicy(string model, EmbeddingBatchProfileStore? store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
        _store = store;
        _stableItems = store?.Read(model) ?? MinimumItems;
        _currentItems = _stableItems;
    }

    public int CurrentItems
    {
        get
        {
            lock (_gate)
            {
                return _currentItems;
            }
        }
    }

    public EmbeddingBatchGrowth? RecordSuccess(int actualItems)
    {
        lock (_gate)
        {
            if (actualItems != _currentItems)
            {
                return null;
            }

            _consecutiveFullSuccesses++;
            if (_consecutiveFullSuccesses < SuccessesBeforeGrowth)
            {
                return null;
            }

            _consecutiveFullSuccesses = 0;
            if (_stableItems != _currentItems || _currentItems == MinimumItems)
            {
                _stableItems = _currentItems;
                _store?.TryWrite(_model, _stableItems);
            }

            if (_currentItems == MaximumItems)
            {
                return null;
            }

            _currentItems *= 2;
            return new EmbeddingBatchGrowth(_stableItems, _currentItems);
        }
    }

    public bool TryStepBack(int failedItems, out int retryItems)
    {
        lock (_gate)
        {
            if (failedItems > _currentItems)
            {
                // A prefetched request was formed before another request lowered the policy.
                retryItems = _currentItems;
                return true;
            }

            retryItems = PreviousStep(failedItems);
            if (retryItems < MinimumItems)
            {
                retryItems = MinimumItems;
                return false;
            }

            _currentItems = retryItems;
            _stableItems = Math.Min(_stableItems, retryItems);
            _consecutiveFullSuccesses = 0;
            _store?.TryWrite(_model, _stableItems);
            return true;
        }
    }

    internal static int Normalize(int value)
    {
        var bounded = Math.Clamp(value, MinimumItems, MaximumItems);
        var normalized = MinimumItems;
        while (normalized <= MaximumItems / 2 && normalized * 2 <= bounded)
        {
            normalized *= 2;
        }

        return normalized;
    }

    private static int PreviousStep(int failedItems)
    {
        var previous = MinimumItems / 2;
        for (var step = MinimumItems; step < failedItems && step <= MaximumItems; step *= 2)
        {
            previous = step;
        }

        return previous;
    }
}
