using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using LocalAi.Documentation;

if (args[0] == "live")
{
#if CACHE_SPLIT
    await LiveCacheProbe.RunAsync(args[1], args[2], args[3], args[4]);
    return;
#else
    throw new InvalidOperationException("Live cache verification requires the candidate implementation.");
#endif
}
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new InvalidOperationException("Measurement root must be new.");
Directory.CreateDirectory(root);
var samples = new List<object>();
await Run(1, 0); // Warm renderer, fonts, validation and JIT; omit from evidence.
foreach (var count in new[] { 1, 5, 12 })
    for (var sample = 1; sample <= 3; sample++) await Run(count, sample);
File.WriteAllText(args[2], JsonSerializer.Serialize(new
{
    SourceHashes = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args[3])),
    CorpusTypes = 12, ModelInvocations = 0, GenerationMode = "deterministic replay; generation timing excludes inference",
    Samples = samples
}, new JsonSerializerOptions { WriteIndented = true }));

async Task Run(int count, int sample)
{
    var fixture = Path.Combine(root, $"types-{count}-sample-{sample}");
    var worktree = Path.Combine(fixture, "worktree"); var runtime = Path.Combine(fixture, "runtime");
    Directory.CreateDirectory(worktree);
    var units = Enumerable.Range(0, count).Select(ProbeData.Unit).ToArray();
    var store = CreateStore(runtime, false);
    await Step("cold");
    await Step("unchanged");
    units = units.Select(u => u with { Content = u.Content with { SourceLine = 47, Snapshot = "snapshot-2" } }).ToArray();
    await Step("presentation");
    store = CreateStore(runtime, true);
    await Step("renderer");
    units = units.Select(u => u with { ReferencesHash = "references-2", Content = u.Content with
    {
        Facts = u.Content.Facts.Select(f => f.Id == "f1" ? f with { Text = "public class B : IDisposable" } : f).ToArray()
    } }).ToArray();
    await Step("dependency");

    async Task Step(string phase)
    {
        DocumentationCacheMetrics.Reset();
        var calls = 0; var generationTicks = 0L;
        var clock = Stopwatch.StartNew();
        var report = await store.BuildAsync(new(worktree, "fixture", units), (input, _) =>
        {
            var start = Stopwatch.GetTimestamp(); calls++;
            var draft = ProbeData.Draft(input);
            generationTicks += Stopwatch.GetTimestamp() - start;
            return Task.FromResult(draft);
        });
        clock.Stop();
        if (report.Failed != 0) throw new InvalidOperationException(JsonSerializer.Serialize(report.Units));
        var stale = 0;
        var hashes = new List<string>();
        var normalizedHashes = new List<string>();
        foreach (var (result, i) in report.Units.Select((unit, i) => (unit, i)))
        {
            var artifact = Path.Combine(runtime, "repositories", ProbeData.RepositoryId, "documentation", "artifacts", result.ArtifactId!);
            var page = File.ReadAllText(Path.Combine(artifact, "page.en.md"));
            if (!page.Contains(units[i].Content.SourcePath + ":" + units[i].Content.SourceLine, StringComparison.Ordinal) ||
                !page.Contains(units[i].Content.Snapshot, StringComparison.Ordinal)) stale++;
            foreach (var path in Directory.GetFiles(artifact).Where(path => !path.EndsWith("artifact.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
                if (path.EndsWith(".pdf", StringComparison.Ordinal))
                {
                    var pdf = Encoding.Latin1.GetString(bytes);
                    const string fonts = @"(?<=/(?:FontName|BaseFont)/)[A-Z]{6}(?=\+)";
                    const string identifiers = @"(?<=<xmpMM:(?:DocumentID|InstanceID)>uuid:)[0-9a-fA-F-]{36}(?=</xmpMM:)";
                    if (Regex.Matches(pdf, fonts).Count == 0 || Regex.Matches(pdf, identifiers).Count != 2)
                        throw new InvalidOperationException("Unexpected PDF metadata; review the comparison boundary.");
                    pdf = Regex.Replace(pdf, fonts, "SUBSET");
                    pdf = Regex.Replace(pdf, identifiers, "00000000-0000-0000-0000-000000000000");
                    bytes = Encoding.Latin1.GetBytes(pdf);
                }
                normalizedHashes.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashes)));
        var normalizedDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalizedHashes)));
        if (sample > 0) samples.Add(new { Types = count, Sample = sample, Phase = phase, BuildMs = clock.Elapsed.TotalMilliseconds,
            GeneratorCalls = calls, GenerationReplayMs = generationTicks * 1000.0 / Stopwatch.Frequency,
            ValidationMs = DocumentationCacheMetrics.ValidationTicks * 1000.0 / Stopwatch.Frequency,
            RenderMs = DocumentationCacheMetrics.RenderTicks * 1000.0 / Stopwatch.Frequency,
            DocumentationCacheMetrics.Validations, DocumentationCacheMetrics.Renders,
            report.Reused, StalePresentations = stale, ArtifactDigest = digest, NormalizedArtifactDigest = normalizedDigest });
        Console.WriteLine($"types={count} sample={sample} phase={phase} build={clock.Elapsed.TotalMilliseconds:F1}ms generation-calls={calls} renders={DocumentationCacheMetrics.Renders} stale={stale}");
    }
}

static DocumentationPackStore CreateStore(string runtime, bool rendererChanged)
{
#if CACHE_SPLIT
    var versions = DocumentationCacheVersions.ForRuntime(runtime);
    if (rendererChanged) versions = versions with { Renderer = "probe-renderer-next" };
    return new(runtime, ProbeData.RepositoryId, "probe", versions);
#else
    return new(runtime, ProbeData.RepositoryId, rendererChanged ? "probe-renderer-next" : "probe");
#endif
}

internal static class ProbeData
{
    internal static string RepositoryId => new('a', 64);
    internal static DocumentationUnitInput Unit(int i) => new(new($"type:A{i}", $"Example.A{i}", $"src/A{i}.cs", 4, "snapshot-1",
        [new("f0", $"type:A{i}", $"public class A{i} : B"), new("f1", "type:B", "public class B")],
        [new("n0", $"Example.A{i}"), new("n1", "Example.B")], [new("n0", "n1", "Implementation")], []), "source-" + i, "references", "history");
    internal static DocumentationDraft Draft(DocumentationInput input) => new(
        [new(["f0", "f1"], "The selected type derives from B.", "Выбранный тип наследует B.")], [],
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"700\" viewBox=\"0 0 1200 700\">" +
        "<g data-symbol-id=\"n0\"><rect x=\"20\" y=\"20\" width=\"400\" height=\"60\"/><text x=\"30\" y=\"60\" font-size=\"20\">" + WebUtility.HtmlEncode(input.Name) + "</text></g>" +
        "<g data-symbol-id=\"n1\"><rect x=\"20\" y=\"180\" width=\"400\" height=\"60\"/><text x=\"30\" y=\"220\" font-size=\"20\">Example.B</text></g>" +
        "<line data-from=\"n0\" data-to=\"n1\" x1=\"200\" y1=\"80\" x2=\"200\" y2=\"180\"/><polygon data-from=\"n0\" data-to=\"n1\" points=\"200,180 190,160 210,160\"/></svg>");
}
