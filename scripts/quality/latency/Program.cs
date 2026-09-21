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

if (args.Length is < 5 or > 6) throw new ArgumentException("Usage: LatencyProbe <root> <corpus> <output.json> <count> <broker.exe> [cache]");
var root = args[0]; var cases = args[1]; var output = args[2];
var limit = args.Length > 3 ? int.Parse(args[3]) : int.MaxValue;
var initial = RuntimeIndexLayout.Inspect(root);
if (initial.DirtyHash is not null) throw new InvalidOperationException("Corpus must be clean");
var corpus = SearchEvaluationCorpus.ResolveAgainstSource(SearchEvaluationCorpus.Load(cases), root);
if (limit < corpus.Cases.Count) corpus = corpus with { Cases = corpus.Cases.Take(limit).ToArray() };
var options = SearchEvaluation.CreateSearchOptions(false) with { ExcludedPaths = SearchEvaluationScope.Create(root, cases, corpus).ExcludedPaths };
var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
var broker = new BrokerClient(new DurableQueue(runtimeRoot), new BrokerProcess(args[4], runtimeRoot));
var active = new AsyncLocal<Sample?>();
var measurements = new List<Sample>();
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
using var search = new SearchService(model => new ProbeClient(model, broker, active.Value!, args.Length > 5 && args[5] == "cache"));
var status = search.Status(root);
var tagsBefore = await Tags();
var document = new { sourceCommit = initial.HeadCommit, sourceTree = initial.HeadTree, initial.DirtyHash,
    corpusSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cases))), status,
    implementation = Environment.GetEnvironmentVariable("CODESEARCH_LATENCY_REVISION") ?? "unrecorded", coreSha256 = Hash(typeof(SearchService).Assembly.Location), brokerSha256 = Hash(args[4]),
    brokerAssemblySha256 = Hash(Path.ChangeExtension(args[4], ".dll")),
    brokerCoreSha256 = Hash(Path.Combine(Path.GetDirectoryName(args[4])!, "LocalAi.Broker.Core.dll")),
    phases = "BeforeEmbedding includes index read/snapshot/overlay/prompt; Embedding includes broker round trip; AfterEmbedding includes normalization/ranking; receipt queue/execution are nested, not additive to client embedding duration.",
    backendLoadMs = (double?)null, backendLoadReason = "not exposed by existing EmbedJobOutput", tagsBefore,
    measurements };
for (var pass = 0; pass < 2; pass++)
{
    var observations = new List<SearchEvaluationObservation>();
    foreach (var item in corpus.Cases)
    {
        var sample = new Sample { Id = item.Id, Pass = pass, Watch = Stopwatch.StartNew() };
        active.Value = sample;
        var outcome = await search.SearchAsync(item.Query, root, options);
        sample.TotalMs = sample.Watch.Elapsed.TotalMilliseconds;
        sample.AfterEmbeddingMs = sample.TotalMs - sample.BeforeEmbeddingMs - sample.EmbeddingMs;
        sample.EmbeddingsUsed = outcome.EmbeddingsUsed;
        sample.Hits = outcome.Hits.Select(SearchEvaluation.FromSearchHit).ToArray();
        measurements.Add(sample);
        observations.Add(new(item.Id, sample.Hits, TimeSpan.FromMilliseconds(sample.TotalMs), sample.QueueMs is {} queue ? TimeSpan.FromMilliseconds(queue) : null));
        File.WriteAllText(output, JsonSerializer.Serialize(document, json));
        Console.WriteLine($"Pass {pass + 1}/2; {observations.Count}/{corpus.Cases.Count}; {item.Id}: total={sample.TotalMs:F1}ms, before={sample.BeforeEmbeddingMs:F1}, embed={sample.EmbeddingMs:F1}, after={sample.AfterEmbeddingMs:F1}, queue={sample.QueueMs:F1}, execution={sample.ExecutionMs:F1}");
    }
    File.WriteAllText(output + $".pass{pass}.quality.json", JsonSerializer.Serialize(SearchEvaluation.Measure(corpus, observations), json));
}
if (initial != RuntimeIndexLayout.Inspect(root)) throw new InvalidOperationException("Source snapshot changed");
var tagsAfter = await Tags();
File.WriteAllText(output + ".tags-after.json", JsonSerializer.Serialize(tagsAfter, json));
Console.WriteLine("COMPLETE. Source snapshot unchanged; model identities captured before and after.");
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
    public bool? WasCold { get; set; }
    public bool EmbeddingsUsed { get; set; }
    public IReadOnlyList<SearchEvaluationHit> Hits { get; set; } = [];
}
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
