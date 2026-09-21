using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed partial class OllamaTransport
{
    // Broker lifetime only; no queries or vectors are persisted. Serialized-vector bytes
    // and entry count are both bounded. Index construction and native embed are unaffected.
    private const int QueryCacheEntries = 128;
    private const int QueryCacheBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _queryCacheGate = new(1, 1);
    private readonly Dictionary<string, LinkedListNode<CachedQuery>> _queryVectors = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedQuery> _queryRecency = new();
    private int _queryBytes;
    private sealed record CachedQuery(string Key, BrokerExecutionResult Result, int Bytes);

    private async Task<BrokerExecutionResult> ExecuteSearchEmbeddingAsync(EmbedJobPayload payload, CancellationToken ct)
    {
        await _queryCacheGate.WaitAsync(ct);
        try
        {
            // Identity is read inside this broker job, not through a second queued job.
            // A mutable model tag alone is never sufficient for a hit.
            var digest = await QueryModelDigestAsync(payload.Model, ct);
            var key = digest is null ? null : Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, payload.Model, Digest = digest,
                    payload.RequestedContextTokens, PreparedQuery = payload.Inputs[0] })));
            ct.ThrowIfCancellationRequested();
            if (key is not null && _queryVectors.TryGetValue(key, out var cached))
            {
                _queryRecency.Remove(cached);
                _queryRecency.AddLast(cached);
                return cached.Value.Result with
                {
                    BackendInputTokens = null,
                    EmbeddingTiming = new EmbeddingBackendTiming(true, null, null, null, null),
                };
            }

            var result = await ExecuteEmbedAsync(payload, ct);
            // A changed/unknown identity or failed/cancelled inference never seeds the cache.
            if (key is not null && digest == await QueryModelDigestAsync(payload.Model, ct))
            {
                ct.ThrowIfCancellationRequested();
                var vector = result.Body.GetProperty("Embeddings")[0];
                double magnitude = 0;
                foreach (var item in vector.EnumerateArray())
                {
                    var value = (float)item.GetDouble();
                    magnitude += value * value;
                }
                var bytes = Encoding.UTF8.GetByteCount(result.Body.GetRawText());
                if (double.IsFinite(magnitude) && magnitude > 0 && bytes <= QueryCacheBytes)
                {
                    while (_queryVectors.Count >= QueryCacheEntries || _queryBytes + bytes > QueryCacheBytes)
                    {
                        var oldest = _queryRecency.First!;
                        _queryRecency.RemoveFirst();
                        _queryVectors.Remove(oldest.Value.Key);
                        _queryBytes -= oldest.Value.Bytes;
                    }
                    _queryVectors.Add(key, _queryRecency.AddLast(new CachedQuery(key, result, bytes)));
                    _queryBytes += bytes;
                }
            }
            return result;
        }
        finally { _queryCacheGate.Release(); }
    }

    private async Task<string?> QueryModelDigestAsync(string model, CancellationToken ct)
    {
        try
        {
            using var document = await SendAsync(HttpMethod.Get, "api/tags", null, [], ct);
            var response = document.RootElement.Deserialize<QueryTags>(ExternalResponseJson);
            if (response?.Models is null) return null;
            var matches = response.Models.Where(entry => entry?.Name == model).ToArray();
            var digest = matches.Length == 1 ? matches[0]?.Digest : null;
            return digest is { Length: 64 } && digest.All(Uri.IsHexDigit) ? digest.ToLowerInvariant() : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            // Metadata is optional for the optimization. Normal embedding still reports its
            // own backend failure; unavailable identity must never authorize cached output.
            return null;
        }
    }

    private sealed record QueryTags([property: JsonPropertyName("models")] IReadOnlyList<QueryModel?>? Models);
    private sealed record QueryModel([property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("digest")] string? Digest);
}
