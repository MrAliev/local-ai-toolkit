using LocalAi.Broker;
using CodeSearch.Core.Indexing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Search;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;

if (args.Length is < 5 or > 6) throw new ArgumentException("Usage: LatencyProbe <root> <corpus> <output.json> <count> <broker.exe> [cache|cold|concurrent]");
var mode = args.Length == 6 ? args[5] : "sequential";
if (mode is not ("sequential" or "cache" or "cold" or "concurrent")) throw new ArgumentException("Unknown probe mode");
var root = args[0]; var cases = args[1]; var output = args[2];
var limit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
var initial = RuntimeIndexLayout.Inspect(root);
if (initial.DirtyHash is not null) throw new InvalidOperationException("Corpus must be clean");
var corpus = SearchEvaluationCorpus.ResolveAgainstSource(SearchEvaluationCorpus.Load(cases), root);
if (limit < corpus.Cases.Count) corpus = corpus with { Cases = corpus.Cases.Take(limit).ToArray() };
var options = SearchEvaluation.CreateSearchOptions(false) with { ExcludedPaths = SearchEvaluationScope.Create(root, cases, corpus).ExcludedPaths };
var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
var policyPath = Path.Combine(runtimeRoot, "settings", "policy.json");
var policyText = File.ReadAllText(policyPath);
var policy = JsonSerializer.Deserialize<JsonElement>(policyText);
var idleSeconds = policy.GetProperty("IdleModelKeepAliveSeconds").GetInt32();
if (mode == "cold" && idleSeconds is < 1 or > 30) throw new InvalidOperationException("Cold probe requires an idle policy between 1 and 30 seconds");
var embeddingTimings = new EmbeddingTimingStore(runtimeRoot);
var broker = new BrokerClient(new DurableQueue(runtimeRoot), new BrokerProcess(args[4], runtimeRoot));
var active = new AsyncLocal<Sample?>();
var measurements = new List<Sample>();
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
using var search = new SearchService(model => new ProbeClient(model, broker, active.Value!, mode == "cache"));
var status = search.Status(root);
var tagsBefore = await Tags();
var runClock = Stopwatch.StartNew();
var document = new { sourceCommit = initial.HeadCommit, sourceTree = initial.HeadTree, initial.DirtyHash,
    mode, concurrency = mode == "concurrent" ? 4 : 1, policy, probeSha256 = Hash(typeof(Sample).Assembly.Location),
    corpusSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cases))), status,
    implementation = Environment.GetEnvironmentVariable("CODESEARCH_LATENCY_REVISION") ?? "unrecorded", coreSha256 = Hash(typeof(SearchService).Assembly.Location), brokerSha256 = Hash(args[4]),
    brokerAssemblySha256 = Hash(Path.ChangeExtension(args[4], ".dll")),
    brokerCoreSha256 = Hash(Path.Combine(Path.GetDirectoryName(args[4])!, "LocalAi.Broker.Core.dll")),
    phases = "BeforeEmbedding includes index read/snapshot/overlay/prompt; Embedding includes broker round trip; AfterEmbedding includes normalization/ranking; receipt queue/execution are nested, not additive to client embedding duration.",
    backendTimingSource = "Optional embedding timing sidecar joined by JobId, read after the search timer stops. Null means unavailable. CacheHit means no backend call; total minus load includes backend overhead, not pure inference.", tagsBefore,
    measurements };
if (mode == "cold")
{
    // A fresh broker has not yet tracked the previous process's resident model.
    // Prime through the broker so its ordinary idle policy owns the unload.
    // This foreground job is outside measurements and does not seed the query cache.
    await new BrokerEmbeddingClient(status.Model, broker).EmbedAsync(["latency probe residency warm-up"],
        LocalJobPriority.Foreground, "latency:prime:" + Guid.NewGuid());
    foreach (var item in corpus.Cases)
    {
        var deadline = Stopwatch.StartNew();
        var residency = await Residency();
        while (residency.ModelPresent)
        {
            if (deadline.Elapsed > TimeSpan.FromMinutes(1)) throw new InvalidOperationException("Model did not become absent under the installed idle policy");
            Console.WriteLine($"Waiting for idle unload before {item.Id}; {deadline.Elapsed.TotalSeconds:F1}s");
            // Frequent metadata jobs would themselves prevent the broker becoming idle.
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds + 2));
            residency = await Residency();
        }
        measurements.Add(await Measure(item.Id, item.Query, 0, residency));
        var warm = await Residency();
        if (!warm.ModelPresent) throw new InvalidOperationException("Model no longer resident before paired warm request");
        measurements.Add(await Measure(item.Id, item.Query, 1, warm));
        File.WriteAllText(output, JsonSerializer.Serialize(document, json));
    }
}
else for (var pass = 0; pass < 2; pass++)
{
    // Separate async execution contexts keep the embedding callback bound to its query.
    // Preserve corpus ordering in the output, regardless of completion order.
    var samples = new Sample[corpus.Cases.Count];
    await Parallel.ForEachAsync(Enumerable.Range(0, samples.Length),
        new ParallelOptions { MaxDegreeOfParallelism = mode == "concurrent" ? 4 : 1 },
        async (index, _) =>
        {
            var item = corpus.Cases[index];
            samples[index] = await Measure(item.Id, item.Query, pass, null);
        });
    measurements.AddRange(samples);
    File.WriteAllText(output, JsonSerializer.Serialize(document, json));
}
for (var pass = 0; pass < 2; pass++)
{
    var observations = measurements.Where(sample => sample.Pass == pass).Select(sample =>
        new SearchEvaluationObservation(sample.Id, sample.Hits, TimeSpan.FromMilliseconds(sample.TotalMs),
            sample.QueueMs is {} queue ? TimeSpan.FromMilliseconds(queue) : null)).ToArray();
    File.WriteAllText(output + $".pass{pass}.quality.json", JsonSerializer.Serialize(SearchEvaluation.Measure(corpus, observations), json));
}
if (initial != RuntimeIndexLayout.Inspect(root)) throw new InvalidOperationException("Source snapshot changed");
if (policyText != File.ReadAllText(policyPath)) throw new InvalidOperationException("Residency policy changed");
var tagsAfter = await Tags();
File.WriteAllText(output + ".tags-after.json", JsonSerializer.Serialize(tagsAfter, json));
Console.WriteLine("COMPLETE. Source snapshot unchanged; model identities captured before and after.");
async Task<Sample> Measure(string id, string query, int pass, ResidencyObservation? residency)
{
    var sample = new Sample { Id = id, Pass = pass, ResidencyBefore = residency,
        StartedAtMs = runClock.Elapsed.TotalMilliseconds, Watch = Stopwatch.StartNew() };
    active.Value = sample;
    var outcome = await search.SearchAsync(query, root, options);
    sample.TotalMs = sample.Watch.Elapsed.TotalMilliseconds;
    sample.AfterEmbeddingMs = sample.TotalMs - sample.BeforeEmbeddingMs - sample.EmbeddingMs;
    sample.BackendTiming = sample.JobId is { } jobId ? embeddingTimings.Read(jobId) : null;
    sample.EmbeddingsUsed = outcome.EmbeddingsUsed;
    if (!outcome.EmbeddingsUsed || sample.JobId is null && mode != "cache")
        throw new InvalidOperationException("A degraded or unmeasured search is not latency evidence");
    sample.Hits = outcome.Hits.Select(SearchEvaluation.FromSearchHit).ToArray();
    Console.WriteLine($"Pass {pass + 1}/2; {id}: total={sample.TotalMs:F1}ms, before={sample.BeforeEmbeddingMs:F1}, embed={sample.EmbeddingMs:F1}, after={sample.AfterEmbeddingMs:F1}, queue={sample.QueueMs:F1}, execution={sample.ExecutionMs:F1}");
    return sample;
}
async Task<ResidencyObservation> Residency()
{
    // Metadata goes through the shared broker. Never infer residency from load duration.
    var request = LocalJobRequestFactory.CreateNativeOllama("latency:processes:" + Guid.NewGuid(), LocalJobPriority.Interactive, NativeOllamaOperation.Processes, null);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await broker.ExecuteAsync<NativeOllamaJobOutput>(request, timeout.Token);
    var matches = result.Value.Response.GetProperty("models").EnumerateArray()
        .Where(entry => entry.GetProperty("name").GetString() == status.Model).ToArray();
    if (matches.Length > 1) throw new InvalidDataException("Ambiguous model residency");
    return new(DateTimeOffset.UtcNow, result.Receipt.JobId, matches.Length == 1,
        matches.Length == 1 ? matches[0].GetProperty("digest").GetString() : null,
        matches.Length == 1 ? matches[0].GetProperty("size_vram").GetInt64() : null);
}
static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
async Task<JsonElement> Tags()
{
    var request = LocalJobRequestFactory.CreateNativeOllama("latency:tags:" + Guid.NewGuid(), LocalJobPriority.Interactive, NativeOllamaOperation.Tags, null);
    return (await broker.ExecuteAsync<NativeOllamaJobOutput>(request)).Value.Response;
}
sealed class Sample
{
    public string Id { get; set; } = "";
    public int Pass { get; set; }
    public double StartedAtMs { get; set; }
    public ResidencyObservation? ResidencyBefore { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Stopwatch Watch { get; set; } = new();
    public double BeforeEmbeddingMs { get; set; }
    public double EmbeddingMs { get; set; }
    public double AfterEmbeddingMs { get; set; }
    public double TotalMs { get; set; }
    public double IdentityMs { get; set; }
    public double IdentityQueueMs { get; set; }
    public double IdentityExecutionMs { get; set; }
    public bool CacheHit { get; set; }
    public double? QueueMs { get; set; }
    public double? ExecutionMs { get; set; }
    public Guid? JobId { get; set; }
    public EmbeddingTimingRecord? BackendTiming { get; set; }
    public bool? WasCold { get; set; }
    public bool EmbeddingsUsed { get; set; }
    public IReadOnlyList<SearchEvaluationHit> Hits { get; set; } = [];
}
sealed record ResidencyObservation(DateTimeOffset ObservedAtUtc, Guid JobId, bool ModelPresent, string? Digest, long? SizeVramBytes);
sealed class ProbeClient(string model, IBrokerClient broker, Sample sample, bool cache) : IEmbeddingClient
{
    public string Model => model;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[][]> Vectors = new();
    private async Task<string?> Digest(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var request = LocalJobRequestFactory.CreateNativeOllama("latency:identity:" + Guid.NewGuid(), LocalJobPriority.Interactive, NativeOllamaOperation.Tags, null);
            var result = await broker.ExecuteAsync<NativeOllamaJobOutput>(request, cancellationToken);
            sample.IdentityQueueMs += result.Receipt.QueueDuration.TotalMilliseconds;
            sample.IdentityExecutionMs += result.Receipt.ExecutionDuration.TotalMilliseconds;
            return result.Value.Response.GetProperty("models").EnumerateArray()
                .Where(entry => entry.GetProperty("name").GetString() == model)
                .Select(entry => entry.GetProperty("digest").GetString()).SingleOrDefault();
        }
        finally { sample.IdentityMs += timer.Elapsed.TotalMilliseconds; }
    }
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, LocalJobPriority priority, string key, CancellationToken cancellationToken = default)
    {
        sample.BeforeEmbeddingMs = sample.Watch.Elapsed.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        try
        {
            var digest = cache ? await Digest(cancellationToken) : null;
            var cacheKey = JsonSerializer.Serialize(new { model, digest, inputs });
            if (digest is not null && Vectors.TryGetValue(cacheKey, out var cached))
            {
                sample.CacheHit = true;
                return cached.Select(vector => vector.ToArray()).ToArray();
            }
            var vectors = await new BrokerEmbeddingClient(model, new ReceiptClient(broker, sample)).EmbedAsync(inputs, priority, key, cancellationToken);
            if (digest is not null && digest == await Digest(cancellationToken))
                Vectors[cacheKey] = vectors.Select(vector => vector.ToArray()).ToArray();
            return vectors;
        }
        finally { sample.EmbeddingMs = watch.Elapsed.TotalMilliseconds; }
    }
}
sealed class ReceiptClient(IBrokerClient broker, Sample sample) : IBrokerClient
{
    public async Task<LocalJobResult<T>> ExecuteAsync<T>(LocalJobRequest request, CancellationToken cancellationToken = default)
    {
        var result = await broker.ExecuteAsync<T>(request, cancellationToken);
        sample.QueueMs = result.Receipt.QueueDuration.TotalMilliseconds;
        sample.ExecutionMs = result.Receipt.ExecutionDuration.TotalMilliseconds;
        sample.JobId = result.Receipt.JobId;
        sample.WasCold = result.Receipt.Routing?.WasCold;
        return result;
    }
}
