using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Documentation;

var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new private fixture directory.");
Directory.CreateDirectory(root);
var rows = new List<object>();
foreach (var count in new[] { 1, 5, 12 }) await Measure(count);
File.WriteAllText(args[1], JsonSerializer.Serialize(new
{
    ModelInvocations = 0, Corpus = "1/5/12 types, eight source revisions, pin revision 1, apply revision 2, keep 2 recent packages",
    Boundary = "logical documentation-store file lengths; deterministic valid draft replay; no model, index, latency or cloud-token claim",
    Rows = rows
}, new JsonSerializerOptions { WriteIndented = true }));

async Task Measure(int count)
{
    var runtime = Path.Combine(root, "types-" + count, "runtime");
    var worktree = Path.Combine(root, "types-" + count, "worktree");
    Directory.CreateDirectory(worktree);
    var repositoryId = new string('a', 64);
    var storage = Path.Combine(runtime, "repositories", repositoryId, "documentation");
    var store = new DocumentationPackStore(runtime, repositoryId, "retention-probe");
    var builds = new List<DocumentationBuildReport>();
    for (var revision = 1; revision <= 8; revision++)
    {
        builds.Add(await Build(revision));
        if (revision == 1) store.Pin(worktree, Path.GetFileName(builds[^1].PackagePath));
        if (revision == 2) store.Apply(worktree, "snapshot-2");
    }
    var worktreeBefore = Hashes(worktree);
    var before = Hashes(storage);
    var beforeBytes = Bytes(storage);
    var preview = store.Prune(worktree, keepPacks: 2);
    RequireEqual(before, Hashes(storage));
    var pruned = store.Prune(worktree, keepPacks: 2, dryRun: false);
    var after = Hashes(storage);
    var afterBytes = Bytes(storage);
    if (beforeBytes - afterBytes != preview.ReclaimableBytes || preview.ReclaimableBytes != pruned.DeletedBytes)
        throw new InvalidOperationException("Reported bytes differ from actual deletion.");
    foreach (var file in after)
        if (!before.TryGetValue(file.Key, out var hash) || hash != file.Value) throw new InvalidOperationException("Retained file changed.");
    RequireEqual(worktreeBefore, Hashes(worktree));
    foreach (var index in new[] { 0, 1, 6, 7 })
    {
        if (!Directory.Exists(builds[index].PackagePath)) throw new InvalidOperationException("Required package deleted.");
        foreach (var unit in builds[index].Units)
            if (!File.Exists(Path.Combine(storage, "artifacts", unit.ArtifactId!, "page.en.html"))) throw new InvalidOperationException("Package link broken.");
    }
    if (store.Prune(worktree, 2).ReclaimableBytes != 0) throw new InvalidOperationException("Cleanup was not stable.");
    var keptPacks = Directory.GetDirectories(Path.Combine(storage, "packs")).Length;
    var keptArtifacts = Directory.GetDirectories(Path.Combine(storage, "artifacts")).Count(path => Path.GetFileName(path) != "_assets");
    var keptTexts = Directory.GetFiles(Path.Combine(storage, "texts"), "*.json").Length;
    await Build(9);
    var next = store.Prune(worktree, 2, dryRun: false);
    if (Directory.GetDirectories(Path.Combine(storage, "packs")).Length != keptPacks ||
        Directory.GetDirectories(Path.Combine(storage, "artifacts")).Count(p => Path.GetFileName(p) != "_assets") != keptArtifacts ||
        Directory.GetFiles(Path.Combine(storage, "texts"), "*.json").Length != keptTexts)
        throw new InvalidOperationException("Retained version count grew after the next revision.");
    RequireEqual(worktreeBefore, Hashes(worktree));
    rows.Add(new { Types = count, BeforeBytes = beforeBytes, AfterBytes = afterBytes, DeletedBytes = pruned.DeletedBytes,
        RetainedPacks = keptPacks, RetainedArtifacts = keptArtifacts, RetainedTextCheckpoints = keptTexts,
        RetainedFilesUnchanged = true, AppliedFilesUnchanged = true, NextRevisionBytes = Bytes(storage), NextDeletedBytes = next.DeletedBytes,
        Plan = preview.Entries.Select(e => new { Path = Path.GetRelativePath(storage, e.Path).Replace('\\', '/'), e.Kind, e.Bytes, e.Delete, e.Reasons }).ToArray() });
    Console.WriteLine($"types={count}: before={beforeBytes}, after={afterBytes}, deleted={pruned.DeletedBytes}, retained-packs={keptPacks}");

    async Task<DocumentationBuildReport> Build(int revision)
    {
        var input = new DocumentationBuildInput(worktree, "snapshot-" + revision, Enumerable.Range(0, count).Select(i => Unit(i, revision)).ToArray());
        var result = await store.BuildAsync(input, (content, _) => Task.FromResult(Draft(content)));
        if (result.Failed != 0) throw new InvalidOperationException("Fixture build failed.");
        var timestamp = new DateTime(2026, 1, 1, 0, 0, revision, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(result.PackagePath, "manifest.json"), timestamp);
        foreach (var unit in result.Units) File.SetLastWriteTimeUtc(Path.Combine(storage, "artifacts", unit.ArtifactId!, "artifact.json"), timestamp);
        return result;
    }
}

static Dictionary<string, string> Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
static long Bytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
static void RequireEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
{
    if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
        throw new InvalidOperationException("Files changed unexpectedly.");
}
static DocumentationUnitInput Unit(int i, int revision) => new(new($"type:A{i}", $"Example.A{i}", $"src/A{i}.cs", 4, "snapshot-" + revision,
    [new("f0", $"type:A{i}", $"public class A{i} : B"), new("f1", "type:B", "public class B")],
    [new("n0", $"Example.A{i}"), new("n1", "Example.B")], [new("n0", "n1", "Implementation")], []), "source-" + revision, "references", "history");
static DocumentationDraft Draft(DocumentationInput input) => new(
    [new(["f0", "f1"], "The selected type derives from B.", "Выбранный тип наследует B.")], [],
    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"700\">" +
    "<g data-symbol-id=\"n0\"><text>" + WebUtility.HtmlEncode(input.Name) + "</text></g>" +
    "<g data-symbol-id=\"n1\"><text>Example.B</text></g><line data-from=\"n0\" data-to=\"n1\"/></svg>");
