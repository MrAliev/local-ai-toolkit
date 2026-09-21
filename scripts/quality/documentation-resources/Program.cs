using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Documentation;

var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new InvalidOperationException("Measurement root must be new.");
Directory.CreateDirectory(root);
var rows = new List<object>();
foreach (var count in new[] { 1, 5, 12 })
{
    await Run(count, false);
#if SHARED_RESOURCES
    await Run(count, true);
#endif
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new {
    SourceHashes = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args[2])),
    ModelInvocations = 0, Corpus = "1/5/12 synthetic public types; deterministic validated draft replay",
    Boundary = "logical file lengths, not allocated filesystem clusters; includes manifests/checkpoints/reviews; excludes installed binaries and models; no timing claim",
    Rows = rows
}, new JsonSerializerOptions { WriteIndented = true }));

async Task Run(int count, bool standalone)
{
    var mode = standalone ? "standalone" : "default";
    var fixture = Path.Combine(root, $"types-{count}-{mode}");
    var worktree = Path.Combine(fixture, "worktree"); var runtime = Path.Combine(fixture, "runtime");
    Directory.CreateDirectory(worktree);
    var repositoryId = new string('a', 64);
#if SHARED_RESOURCES
    var store = new DocumentationPackStore(runtime, repositoryId, "probe", htmlMode: standalone ? DocumentationHtmlMode.Standalone : DocumentationHtmlMode.Shared);
#else
    var store = new DocumentationPackStore(runtime, repositoryId, "probe");
#endif
    for (var revision = 1; revision <= 3; revision++)
    {
        var calls = 0;
        var units = Enumerable.Range(0, count).Select(i => Unit(i, revision)).ToArray();
        var report = await store.BuildAsync(new(worktree, "snapshot-" + revision, units), (input, _) => { calls++; return Task.FromResult(Draft(input)); });
        if (report.Failed != 0 || calls != (revision == 1 ? count : 0)) throw new InvalidOperationException("Build or text reuse failed.");
        var hashes = new List<string>();
        foreach (var unit in report.Units)
        {
            var artifact = Path.Combine(runtime, "repositories", repositoryId, "documentation", "artifacts", unit.ArtifactId!);
            foreach (var path in Directory.GetFiles(artifact).Where(p => !p.EndsWith("artifact.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                if (path.EndsWith(".html", StringComparison.Ordinal))
                {
                    var html = Encoding.UTF8.GetString(bytes);
                    var linkedCss = Regex.Match(html, "<link rel=\"stylesheet\" href=\"(../_assets/[a-f0-9]{64}.css)\">");
                    if (linkedCss.Success)
                    {
                        var cssBytes = File.ReadAllBytes(Path.Combine(artifact, linkedCss.Groups[1].Value));
                        if (Path.GetFileNameWithoutExtension(linkedCss.Groups[1].Value) != Convert.ToHexStringLower(SHA256.HashData(cssBytes)))
                            throw new InvalidOperationException("Shared resource is not content addressed.");
                        html = html.Replace(linkedCss.Value, "", StringComparison.Ordinal).Replace("<style>", "<style>" + Encoding.UTF8.GetString(cssBytes), StringComparison.Ordinal);
                    }
                    // Require both fonts to resolve to the exact bundled bytes before normalizing their location.
                    var matches = Regex.Matches(html, @"url\((data:font/ttf;base64,[A-Za-z0-9+/=]+|\.\./_assets/[a-f0-9]{64}\.ttf)\)");
                    if (matches.Count != 2) throw new InvalidOperationException("Expected two font resources.");
                    var assembly = typeof(DocumentationRenderer).Assembly;
                    foreach (var (match, i) in matches.Cast<Match>().Select((m, i) => (m, i)))
                    {
                        var url = match.Groups[1].Value;
                        var actual = url.StartsWith("data:", StringComparison.Ordinal) ? Convert.FromBase64String(url.Split(',')[1]) : File.ReadAllBytes(Path.Combine(artifact, url));
                        using var embedded = assembly.GetManifestResourceStream("LocalAi.Documentation.Fonts." + (i == 0 ? "NotoSans-Regular.ttf" : "NotoSansMono-Regular.ttf"))!;
                        using var expected = new MemoryStream(); embedded.CopyTo(expected);
                        if (!actual.SequenceEqual(expected.ToArray())) throw new InvalidOperationException("Font content changed.");
                    }
                    html = Regex.Replace(html, @"url\((data:font/ttf;base64,[A-Za-z0-9+/=]+|\.\./_assets/[a-f0-9]{64}\.ttf)\)", "url(FONT)");
                    html = html.Replace("src=\"data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(artifact, "diagram.png"))) + "\"", "src=\"diagram.svg\"", StringComparison.Ordinal);
                    html = html.Replace("<details><summary>LICENSES.txt</summary><pre>" + WebUtility.HtmlEncode(File.ReadAllText(Path.Combine(artifact, "LICENSES.txt"))) + "</pre></details>", "<p><a href=\"LICENSES.txt\">LICENSES.txt</a></p>", StringComparison.Ordinal);
                    bytes = Encoding.UTF8.GetBytes(html);
                }
                if (path.EndsWith(".pdf", StringComparison.Ordinal))
                {
                    var pdf = Encoding.Latin1.GetString(bytes);
                    const string fonts = @"(?<=/(?:FontName|BaseFont)/)[A-Z]{6}(?=\+)";
                    const string identifiers = @"(?<=<xmpMM:(?:DocumentID|InstanceID)>uuid:)[0-9a-fA-F-]{36}(?=</xmpMM:)";
                    if (Regex.Matches(pdf, fonts).Count == 0 || Regex.Matches(pdf, identifiers).Count != 2) throw new InvalidOperationException("Unexpected PDF metadata.");
                    pdf = Regex.Replace(pdf, fonts, "SUBSET");
                    pdf = Regex.Replace(pdf, identifiers, "00000000-0000-0000-0000-000000000000");
                    bytes = Encoding.Latin1.GetBytes(pdf);
                }
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        var applied = store.Apply(worktree, "snapshot-" + revision);
        if (applied.Skipped.Count != 0) throw new InvalidOperationException("Unexpected manual edits.");
        foreach (var page in Directory.GetFiles(worktree, "*.html", SearchOption.AllDirectories))
        foreach (Match font in Regex.Matches(File.ReadAllText(page), @"href=""(\.\./_assets/[a-f0-9]{64}\.css)"""))
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(page)!, font.Groups[1].Value))) throw new InvalidOperationException("Applied font link is broken.");
        var storage = Path.Combine(runtime, "repositories", repositoryId, "documentation");
        var files = Directory.GetFiles(storage, "*", SearchOption.AllDirectories).Select(p => new FileInfo(p)).ToArray();
        var appliedFiles = Directory.GetFiles(worktree, "*", SearchOption.AllDirectories).Select(p => new FileInfo(p)).ToArray();
        rows.Add(new { Types = count, Revision = revision, Mode = mode, GeneratorCalls = calls,
            StorageBytes = files.Sum(f => f.Length), HtmlBytes = files.Where(f => f.Name.StartsWith("page.", StringComparison.Ordinal) && f.Extension == ".html").Sum(f => f.Length),
            ResourceFiles = files.Count(f => f.Extension == ".css"), ResourceBytes = files.Where(f => f.Extension == ".css").Sum(f => f.Length),
            AppliedBytes = appliedFiles.Sum(f => f.Length), AppliedResources = appliedFiles.Count(f => f.Extension == ".css"),
            ContentDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashes))) });
        Console.WriteLine($"types={count} mode={mode} revision={revision} bytes={files.Sum(f => f.Length)} generation-calls={calls}");
    }
}

static DocumentationUnitInput Unit(int i, int revision) => new(new($"type:A{i}", $"Example.A{i}", $"src/A{i}.cs", 4, "snapshot-" + revision,
    [new("f0", $"type:A{i}", $"public class A{i} : B"), new("f1", "type:B", "public class B")],
    [new("n0", $"Example.A{i}"), new("n1", "Example.B")], [new("n0", "n1", "Implementation")], []), "source-" + i, "references", "history");
static DocumentationDraft Draft(DocumentationInput input) => new(
    [new(["f0", "f1"], "The selected type derives from B.", "Выбранный тип наследует B.")], [],
    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"700\">" +
    "<g data-symbol-id=\"n0\"><text>" + WebUtility.HtmlEncode(input.Name) + "</text></g>" +
    "<g data-symbol-id=\"n1\"><text>Example.B</text></g>" +
    "<line data-from=\"n0\" data-to=\"n1\"/></svg>");
