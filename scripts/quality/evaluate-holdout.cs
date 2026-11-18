#:project ../../src/CodeSearch.Core/CodeSearch.Core.csproj
#:property PublishAot=false

using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeSearch.Core.Search;
using CodeSearch.Core.Indexing;

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, WriteIndented = true };
json.Converters.Add(new JsonStringEnumConverter());
if (args is not ["validate", _, _] && args is not ["compare", _, _, _, _, _, _])
{
    Console.Error.WriteLine("Usage: validate <protocol> <prepared-root> | compare <protocol> <prepared-root> <frozen-artifact-plan> <baseline-root> <candidate-root> <output.json>");
    return 2;
}
var protocolBytes = File.ReadAllBytes(args[1]);
using var protocolDocument = JsonDocument.Parse(protocolBytes);
var protocol = protocolDocument.RootElement;
if (protocol.GetProperty("schemaVersion").GetInt32() != 1 || protocol.GetProperty("suite").GetString() != "heldout-v2")
    throw new InvalidDataException("Unknown holdout protocol.");
var prepared = Path.GetFullPath(args[2]);
using var preparationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared, "preparation.json")));
var preparation = preparationDocument.RootElement;
if (preparation.GetProperty("schemaVersion").GetInt32() != 1 || preparation.GetProperty("searchSuite").GetString() != "heldout-v2")
    throw new InvalidDataException("Prepared source suite mismatch.");
var names = new[] { "csharp", "typescript", "python" };
var corpora = new Dictionary<string, SearchEvaluationCorpus>();
var groups = new Dictionary<string, SearchEvaluationIntentGroup[]>();
foreach (var name in names)
{
    var path = Path.Combine(prepared, "results", name, "cases.json");
    var corpus = SearchEvaluationCorpus.ResolveAgainstSource(SearchEvaluationCorpus.Load(path), Path.Combine(prepared, "sources", name));
    if (args[0] == "compare")
    {
        var current = RuntimeIndexLayout.Inspect(Path.Combine(prepared, "sources", name));
        var expected = preparation.GetProperty("corpora").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
        if (current.DirtyHash is not null || current.SemanticDirtyHash is not null
            || current.HeadCommit != expected.GetProperty("sourceCommit").GetString()
            || current.HeadTree != expected.GetProperty("sourceTree").GetString())
            throw new InvalidDataException("Prepared source bytes/current identity changed before scoring.");
    }
    var pairs = protocol.GetProperty("groups").EnumerateArray()
        .Where(item => item.GetProperty("corpus").GetString() == name)
        .Select(item => new SearchEvaluationIntentGroup(item.GetProperty("intentId").GetString()!,
            item.GetProperty("cases").EnumerateArray().Select(value => value.GetString()!).ToArray())).ToArray();
    SearchEvaluationIntentGroups.ValidateGroups(corpus, pairs);
    foreach (var pair in pairs)
    {
        if (pair.CaseIds.Count(id => id.EndsWith("-en", StringComparison.Ordinal)) != 1
            || pair.CaseIds.Count(id => id.EndsWith("-ru", StringComparison.Ordinal)) != 1)
            throw new InvalidDataException("Each intent must have one EN and one RU paraphrase.");
    }
    corpora.Add(name, corpus);
    groups.Add(name, pairs);
}
var requestedModel = protocol.GetProperty("config").GetProperty("requestedModel").GetString()!;
var actualOptions = SearchQualityProfile.Resolve(requestedModel, SearchEvaluation.CreateSearchOptions(false));
if (args[0] == "validate")
{
    Console.WriteLine(JsonSerializer.Serialize(new { validated = true, retrievalExecuted = false,
        queries = corpora.Values.Sum(item => item.Cases.Count), intents = groups.Values.Sum(item => item.Length),
        fullSearchOptions = actualOptions, qualityProfile = SearchQualityProfile.Require(requestedModel),
        preparedInputs = names.ToDictionary(name => name, name => new {
            candidateScope = SearchEvaluationScope.Create(Path.Combine(prepared, "sources", name), Path.Combine(prepared, "results", name, "cases.json"), corpora[name]),
            annotations = corpora[name].Cases.Select(item => new { item.Id, item.Relevant }) }) }, json));
    return 0;
}
if (protocol.GetProperty("status").GetString() != "frozen-unmeasured")
    throw new InvalidDataException("Freeze the reviewed protocol before comparing retrieval results.");
if (protocol.GetProperty("config").GetProperty("modelDigest").ValueKind != JsonValueKind.Null)
    throw new InvalidDataException("This evaluator has requested-tag metadata only; it cannot verify a model weight digest.");
var artifactPlanBytes = File.ReadAllBytes(args[3]);
using var artifactPlanDocument = JsonDocument.Parse(artifactPlanBytes);
var artifactPlan = artifactPlanDocument.RootElement;
var protocolHash = Convert.ToHexStringLower(SHA256.HashData(protocolBytes));
if (artifactPlan.GetProperty("schemaVersion").GetInt32() != 1 || artifactPlan.GetProperty("protocolSha256").GetString() != protocolHash)
    throw new InvalidDataException("Artifact plan does not bind this frozen protocol.");
var allCases = new List<SearchEvaluationCase>();
var allGroups = new List<SearchEvaluationIntentGroup>();
var allBaseline = new List<SearchEvaluationObservation>();
var allCandidate = new List<SearchEvaluationObservation>();
var mismatches = new List<string>();
var reports = new List<object>();
var identityFields = new[] { "schemaVersion", "mode", "caseCount", "repository", "generationId", "model", "sourceCommit", "sourceTree",
    "dirtyHash", "indexedCommit", "indexedTree", "corpusSha256", "candidateScope", "searchOptions", "useQueryInstruction", "qualityProfile", "annotations" };
foreach (var name in names)
{
    using var baselineDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[4], "results", name, "baseline.json")));
    using var candidateDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[5], "results", name, "baseline.json")));
    var baseline = baselineDocument.RootElement;
    var candidate = candidateDocument.RootElement;
    foreach (var field in identityFields)
        if (!(field == "qualityProfile" ? SameProfile(baseline.GetProperty(field), candidate.GetProperty(field))
            : Equal(baseline.GetProperty(field), candidate.GetProperty(field))))
            throw new InvalidDataException($"Holdout comparison identity/config mismatch: {name}/{field}.");
    var preparedIdentity = preparation.GetProperty("corpora").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
    var sourceRoot = Path.Combine(prepared, "sources", name);
    var casesPath = Path.Combine(prepared, "results", name, "cases.json");
    if (!string.Equals(baseline.GetProperty("repository").GetString(), sourceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || baseline.GetProperty("caseCount").GetInt32() != corpora[name].Cases.Count
        || baseline.GetProperty("sourceCommit").GetString() != preparedIdentity.GetProperty("sourceCommit").GetString()
        || baseline.GetProperty("indexedCommit").GetString() != preparedIdentity.GetProperty("sourceCommit").GetString()
        || baseline.GetProperty("sourceTree").GetString() != preparedIdentity.GetProperty("sourceTree").GetString()
        || baseline.GetProperty("indexedTree").GetString() != preparedIdentity.GetProperty("sourceTree").GetString()
        || baseline.GetProperty("dirtyHash").ValueKind != JsonValueKind.Null
        || baseline.GetProperty("sourceTree").GetString() != protocol.GetProperty("freeze").GetProperty("sourceTrees").GetProperty(name).GetString()
        || baseline.GetProperty("generationId").GetString() != protocol.GetProperty("freeze").GetProperty("generationIds").GetProperty(name).GetString())
        throw new InvalidDataException("Measured source/index identity differs from the frozen preparation.");
    var expectedAnnotations = JsonSerializer.SerializeToElement(corpora[name].Cases.Select(item => new { item.Id, item.Relevant }), json);
    var expectedScope = JsonSerializer.SerializeToElement(SearchEvaluationScope.Create(sourceRoot, casesPath, corpora[name]), json);
    if (!Equal(baseline.GetProperty("annotations"), expectedAnnotations) || !Equal(baseline.GetProperty("candidateScope"), expectedScope))
        throw new InvalidDataException("Measured scope/references differ from the reviewed prepared corpus.");
    var caseHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(prepared, "results", name, "cases.json"))));
    if (baseline.GetProperty("corpusSha256").GetString() != caseHash
        || protocol.GetProperty("freeze").GetProperty("corpusHashes").GetProperty(name).GetString() != caseHash)
        throw new InvalidDataException("Frozen corpus hash mismatch.");
    if (baseline.GetProperty("model").GetString() != requestedModel || baseline.GetProperty("mode").GetString() != "profile"
        || !baseline.GetProperty("useQueryInstruction").GetBoolean()
        || !Equal(baseline.GetProperty("searchOptions"), protocol.GetProperty("config").GetProperty("fullSearchOptions"))
        || !SameProfile(baseline.GetProperty("qualityProfile"), protocol.GetProperty("config").GetProperty("qualityProfile")))
        throw new InvalidDataException("Measured configuration differs from the frozen configuration.");
    var before = baseline.GetProperty("observations").Deserialize<SearchEvaluationObservation[]>(json)!;
    var after = candidate.GetProperty("observations").Deserialize<SearchEvaluationObservation[]>(json)!;
    var corpus = corpora[name];
    var pairMetricsBefore = SearchEvaluationIntentGroups.Measure(corpus, before, groups[name]);
    var pairMetricsAfter = SearchEvaluationIntentGroups.Measure(corpus, after, groups[name]);
    var beforeById = before.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
    var afterById = after.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
    foreach (var item in corpus.Cases)
        if (!Equal(JsonSerializer.SerializeToElement(beforeById[item.Id].Hits, json), JsonSerializer.SerializeToElement(afterById[item.Id].Hits, json)))
            mismatches.Add(name + ":" + item.Id);
    var locales = new Dictionary<string, object>();
    foreach (var locale in new[] { "en", "ru" })
    {
        var subset = corpus with { Cases = corpus.Cases.Where(item => item.Id.EndsWith("-" + locale, StringComparison.Ordinal)).ToArray() };
        var ids = subset.Cases.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        locales.Add(locale, new { baseline = SearchEvaluation.Measure(subset, before.Where(item => ids.Contains(item.CaseId)).ToArray()),
            candidate = SearchEvaluation.Measure(subset, after.Where(item => ids.Contains(item.CaseId)).ToArray()) });
    }
    reports.Add(new { corpus = name, queries = corpus.Cases.Count, groups = groups[name].Length,
        identity = identityFields.ToDictionary(field => field, field => baseline.GetProperty(field).Clone()),
        baseline = SearchEvaluation.Measure(corpus, before), candidate = SearchEvaluation.Measure(corpus, after),
        baselineIntents = pairMetricsBefore, candidateIntents = pairMetricsAfter, locales });
    allCases.AddRange(corpus.Cases); allGroups.AddRange(groups[name]); allBaseline.AddRange(before); allCandidate.AddRange(after);
}
var combined = new SearchEvaluationCorpus(1, allCases);
using var baselineRun = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[4], "run.json")));
using var candidateRun = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[5], "run.json")));
foreach (var (name, run) in new[] { ("baseline", baselineRun.RootElement), ("candidate", candidateRun.RootElement) })
{
    if (!run.GetProperty("completed").GetBoolean() || !run.GetProperty("binaryIdentityStable").GetBoolean()
        || run.GetProperty("codeSearch").GetArrayLength() == 0)
        throw new InvalidDataException("A complete run with stable observed CLI/DLL fingerprints is required.");
    if (!Equal(run.GetProperty("codeSearch"), run.GetProperty("codeSearchAfter"))
        || !Equal(run.GetProperty("codeSearch"), artifactPlan.GetProperty(name).GetProperty("codeSearch")))
        throw new InvalidDataException("Observed before/after binaries differ from the frozen artifact plan.");
}
var report = new { schemaVersion = 1, suite = "heldout-v2", rankingChange = "none_harness_parity_only", parityPassed = mismatches.Count == 0,
    mismatches, queryCount = allCases.Count, intentCount = allGroups.Count, protocolSha256 = protocolHash,
    artifactPlanSha256 = Convert.ToHexStringLower(SHA256.HashData(artifactPlanBytes)),
    baselineBinaries = baselineRun.RootElement.GetProperty("codeSearch").Clone(), candidateBinaries = candidateRun.RootElement.GetProperty("codeSearch").Clone(),
    groupedBaseline = SearchEvaluationIntentGroups.Measure(combined, allBaseline, allGroups),
    groupedCandidate = SearchEvaluationIntentGroups.Measure(combined, allCandidate, allGroups), corpora = reports,
    limitations = new[] { "AI-authored references with blind agent review; human validation not established", "Synthetic correlated templates; ranges are descriptive independence references",
        "No ranking change or quality improvement claim", "Latency descriptive; shared broker load/cold-warm state uncontrolled; queue wait may be unavailable",
        "Historical v1 metrics use different inputs", "Requested model tag is controlled; model weight digest and per-request routing proof unavailable",
        "This suite is a known regression corpus after its first measured use" } };
using (var file = new FileStream(args[6], FileMode.CreateNew, FileAccess.Write)) JsonSerializer.Serialize(file, report, json);
Console.WriteLine(JsonSerializer.Serialize(new { report.parityPassed, report.queryCount, report.intentCount, mismatchCount = mismatches.Count }, json));
return mismatches.Count == 0 ? 0 : 1;

static bool Equal(JsonElement a, JsonElement b) => JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));

// The calibration instant is metadata. PowerShell JSON round trips can change its
// offset representation without changing that instant; no other field is relaxed.
static bool SameProfile(JsonElement a, JsonElement b)
{
    if (!Instant(a, out var before) || !Instant(b, out var after) || before != after) return false;
    var left = JsonNode.Parse(a.GetRawText())!.AsObject();
    var right = JsonNode.Parse(b.GetRawText())!.AsObject();
    left.Remove("calibratedAtUtc"); right.Remove("calibratedAtUtc");
    return JsonNode.DeepEquals(left, right);

    static bool Instant(JsonElement profile, out DateTimeOffset value)
    {
        value = default;
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("calibratedAtUtc", out var timestamp)
            || timestamp.ValueKind != JsonValueKind.String) return false;
        var text = timestamp.GetString()!;
        return Regex.IsMatch(text, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
