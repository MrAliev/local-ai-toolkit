using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSearch.Core.Chunking;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalAi.Repository;

if (args.Length != 4) throw new ArgumentException("Usage: ExactSymbolProbe <clean-root> <cases.json> <output.json> <broker.exe>");
var root = args[0];
var corpusPath = args[1];
var outputPath = args[2];
var runtimeRoot = ModelResidencyPolicyStore.DefaultRuntimeRoot;
var initial = RuntimeIndexLayout.Inspect(root);
if (initial.DirtyHash is not null) throw new InvalidOperationException("Corpus must be clean");
var corpus = SearchEvaluationCorpus.ResolveAgainstSource(SearchEvaluationCorpus.Load(corpusPath), root);
var options = SearchEvaluation.CreateSearchOptions(false) with
{
    ExcludedPaths = SearchEvaluationScope.Create(root, corpusPath, corpus).ExcludedPaths,
};
var broker = new BrokerClient(new DurableQueue(runtimeRoot), new BrokerProcess(args[3], runtimeRoot));
using var search = new SearchService(model => new BrokerEmbeddingClient(model, broker));
var status = search.Status(root);
if (!status.Exists || status.RequiresOverlay || status.CommitDrifted)
    throw new InvalidOperationException("Pilot requires a current, clean base generation without overlays");
var index = search.Load(status.IndexPath);
var generation = index.GenerationId;
var resolvedOptions = SearchQualityProfile.Resolve(index.Model, options);
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var before = await Tags();
var digest = Digest(before, status.Model);
var policyPath = Path.Combine(runtimeRoot, "settings", "policy.json");
var policyText = File.ReadAllText(policyPath);
var hostBefore = HostIdentity();
var cases = corpus.Cases.Select(item => new PilotCase("natural:" + item.Id, item.Query, "original-corpus")).ToList();
using (var source = JsonDocument.Parse(File.ReadAllText(corpusPath)))
{
    var symbols = source.RootElement.GetProperty("cases").EnumerateArray()
        .SelectMany(item => item.GetProperty("relevant").EnumerateArray())
        .Select(item => item.GetProperty("symbol").GetString()!)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    foreach (var symbol in symbols)
        cases.Add(new("qualified:" + symbol, symbol, "qualified-label"));
    foreach (var symbol in symbols.Select(ShortName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        cases.Add(new("short:" + symbol, symbol, "short-label"));
}
cases.Add(new("missing", "MissingSymbolForExactPilot", "absent-symbol"));
var samples = new List<Observation>();
for (var pass = 0; pass < 2; pass++)
{
    foreach (var item in cases)
    {
        // Both arms inspect the current source. The prototype deliberately supports only
        // this clean-base fixture: production overlays/filters/navigation need separate design.
        var timer = Stopwatch.StartNew();
        if (initial != RuntimeIndexLayout.Inspect(root)) throw new InvalidOperationException("Corpus changed");
        var currentIndex = search.Load(status.IndexPath);
        if (currentIndex.GenerationId != generation) throw new InvalidOperationException("Generation changed");
        var matches = IsIdentifier(item.Query)
            ? currentIndex.Chunks.Where(chunk => chunk.Kind != ChunkKind.Text &&
                (chunk.Symbol == item.Query || ShortName(chunk.Symbol) == item.Query)).ToArray()
            : [];
        // More than one chunk is conservatively ambiguous, including partitioned bodies.
        // A chunk display name does not establish a unique semantic definition.
        SearchHit[] exact = [];
        if (matches.Length == 1)
        {
            var target = matches[0];
            exact = SearchEngine.SearchLexically(currentIndex, item.Query,
                    resolvedOptions with { TopK = currentIndex.Chunks.Count, MaxPerFile = currentIndex.Chunks.Count }, root)
                .Where(hit => hit.RelPath == currentIndex.Files[target.FileIndex].RelPath &&
                    hit.StartLine == target.StartLine && hit.EndLine == target.EndLine && hit.Symbol == target.Symbol)
                .ToArray();
            if (exact.Length != 1) throw new InvalidOperationException("Unique named chunk did not yield its verified snippet");
        }
        var prototypeMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var hybrid = await search.SearchAsync(item.Query, root, options);
        var hybridMs = timer.Elapsed.TotalMilliseconds;
        if (!hybrid.EmbeddingsUsed) throw new InvalidOperationException("Degraded hybrid result is not evidence");
        var mode = !IsIdentifier(item.Query) ? "ineligible" : matches.Length switch { 0 => "not-found", 1 => "unique-chunk", _ => "ambiguous-chunks" };
        samples.Add(new(item.Id, item.Query, item.Category, pass, mode, matches.Length, prototypeMs, hybridMs,
            exact.Select(Location).ToArray(), hybrid.Hits.Select(Location).ToArray(),
            HashText(JsonSerializer.Serialize(hybrid.Hits.Select(SearchEvaluation.FromSearchHit), json)),
            exact.Length == 1 && hybrid.Hits.FirstOrDefault()?.ChunkId == exact[0].ChunkId,
            exact.Select(hit => hit.ChunkId).SequenceEqual(hybrid.Hits.Select(hit => hit.ChunkId))));
        Console.WriteLine($"Pass {pass + 1}/2; {samples.Count}/{cases.Count * 2}; {item.Id}: {mode}, matches={matches.Length}, prototype={prototypeMs:F1}ms, hybrid={hybridMs:F1}ms");
    }
}
if (initial != RuntimeIndexLayout.Inspect(root) || search.Status(root).IndexPath != status.IndexPath)
    throw new InvalidOperationException("Source or generation changed");
if (policyText != File.ReadAllText(policyPath) || hostBefore != HostIdentity())
    throw new InvalidOperationException("Policy or measured broker changed");
var after = await Tags();
if (digest != Digest(after, status.Model)) throw new InvalidOperationException("Model identity changed");
foreach (var group in samples.GroupBy(sample => sample.Id))
{
    var pair = group.OrderBy(sample => sample.Pass).ToArray();
    if (pair.Length != 2 || pair[0].HybridHitsSha256 != pair[1].HybridHitsSha256 ||
        pair[0].Decision != pair[1].Decision || !pair[0].Exact.SequenceEqual(pair[1].Exact))
        throw new InvalidOperationException("Repeated retrievals or prototype decisions differ");
}
var document = new
{
    schemaVersion = 1, complete = true,
    implementation = Environment.GetEnvironmentVariable("CODESEARCH_LATENCY_REVISION") ?? "unrecorded",
    coreSha256 = HashFile(typeof(SearchService).Assembly.Location), probeSha256 = HashFile(typeof(PilotCase).Assembly.Location),
    brokerSha256 = HashFile(args[3]), broker = hostBefore,
    sourceCommit = initial.HeadCommit, sourceTree = initial.HeadTree, generation,
    corpusSha256 = HashFile(corpusPath), model = status.Model, digestBefore = digest, digestAfter = Digest(after, status.Model),
    policy = JsonSerializer.Deserialize<JsonElement>(policyText), caseCount = cases.Count,
    boundaries = "Prototype: one fresh Git inspection, cached base index, conservative exact-name decision, lexical scoring and verified snippet when unique. Hybrid: whole SearchAsync. Model startup/cache state is uncontrolled; pass 1 repeats queries. Prototype timing excludes any hybrid fallback. No production shortcut is enabled.",
    samples,
};
File.WriteAllText(outputPath, JsonSerializer.Serialize(document, json) + "\n");
Console.WriteLine($"COMPLETE: {samples.Count} observations; identical repeated hybrid hits; stable source, generation, broker, model and policy.");

static bool IsIdentifier(string query) => Regex.IsMatch(query, @"^[\p{L}_][\p{L}\p{N}_.]*$", RegexOptions.CultureInvariant);
static string ShortName(string symbol) => symbol[(symbol.LastIndexOf('.') + 1)..];
static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static HitLocation Location(SearchHit hit) => new(hit.RelPath.Replace('\\', '/'), hit.Symbol, hit.StartLine, hit.EndLine);
static string Digest(JsonElement tags, string model) => tags.GetProperty("models").EnumerateArray()
    .Where(entry => entry.GetProperty("name").GetString() == model).Select(entry => entry.GetProperty("digest").GetString()!)
    .Single() ?? throw new InvalidDataException("Missing model digest");
async Task<JsonElement> Tags()
{
    var request = LocalJobRequestFactory.CreateNativeOllama("exact-pilot:tags:" + Guid.NewGuid(), LocalJobPriority.Interactive, NativeOllamaOperation.Tags, null);
    return (await broker.ExecuteAsync<NativeOllamaJobOutput>(request)).Value.Response;
}
BrokerIdentity HostIdentity()
{
    using var host = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "host.json")));
    var value = host.RootElement;
    if (!string.Equals(Path.GetFullPath(value.GetProperty("BrokerAssemblyPath").GetString()!), Path.GetFullPath(args[3]), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Requested broker is not serving the runtime");
    return new(value.GetProperty("ProcessId").GetInt32(), value.GetProperty("StartedAtUtc").GetDateTimeOffset());
}
sealed record PilotCase(string Id, string Query, string Category);
sealed record HitLocation(string Path, string Symbol, int StartLine, int EndLine);
sealed record BrokerIdentity(int ProcessId, DateTimeOffset StartedAtUtc);
sealed record Observation(string Id, string Query, string Category, int Pass, string Decision, int MatchingChunks,
    double PrototypeMs, double HybridMs, HitLocation[] Exact, HitLocation[] Hybrid,
    string HybridHitsSha256, bool SameFirstHit, bool SameOrderedLocations);
