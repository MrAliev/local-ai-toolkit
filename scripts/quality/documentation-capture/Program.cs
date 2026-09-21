using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Semantics;
using LocalAi.Documentation;

var json = new JsonSerializerOptions { WriteIndented = true };
if (args[0] == "prepare")
{
    // The caller extracts the frozen source archive into a new fixture root first.
    var root = Path.GetFullPath(args[1]);
    var runtime = Path.GetFullPath(args[2]);
    Git(root, "init", "-b", "main");
    Git(root, "config", "user.name", "Documentation probe");
    Git(root, "config", "user.email", "probe@example.invalid");
    Git(root, "config", "core.autocrlf", "false");
    Git(root, "add", ".");
    Git(root, "-c", "core.hooksPath=", "commit", "-m", "frozen documentation corpus");
    var identity = RuntimeIndexLayout.Inspect(root, runtime);
    var original = JsonSerializer.Deserialize<GenerationManifest>(File.ReadAllText(Path.Combine(args[3], "manifest.json")))!;
    var generation = original.Identity with { RepositoryId = identity.RepositoryId, DevCommit = identity.HeadCommit, DevTree = identity.HeadTree };
    var old = CodeIndex.Load(Path.Combine(args[3], "base.cidx"));
    var corpus = new CodeIndex
    {
        Root = root, RepositoryId = identity.RepositoryId, GenerationId = generation.Id,
        GitCommit = identity.HeadCommit, GitTree = identity.HeadTree, IndexedAtUtc = old.IndexedAtUtc,
        Model = old.Model, Dim = old.Dim, Files = old.Files, Chunks = old.Chunks, Vectors = old.Vectors
    };
    Directory.CreateDirectory(runtime);
    var corpusPath = Path.Combine(runtime, "fixture.cidx");
    corpus.Save(corpusPath);
    var semantic = SemanticIndex.Load(Path.Combine(args[3], "semantic.sidx")) with
    {
        RepositoryId = identity.RepositoryId, GenerationId = generation.Id, GitTree = identity.HeadTree,
        BaseCommit = identity.HeadCommit, DirtyHash = null
    };
    var semanticPath = Path.Combine(runtime, "fixture.sidx");
    semantic.Save(semanticPath);
    var store = new GenerationStore(identity.RepositoryRuntimeRoot);
    store.SetCurrent(store.PublishIndex(corpusPath, generation, semanticPath));
    Console.WriteLine($"Fixture: {semantic.Documents.Count} semantic documents; {old.Chunks.Count} chunks; {new FileInfo(corpusPath).Length} corpus bytes");
    return;
}

var samples = new List<object>();
foreach (var limit in new[] { 1, 5, int.MaxValue })
{
    _ = DocumentationWorkspace.Capture(args[1], args[2], limit);
    for (var sample = 1; sample <= 3; sample++)
    {
        DocumentationCaptureMetrics.Reset();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var input = DocumentationWorkspace.Capture(args[1], args[2], limit);
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input.Units)));
        samples.Add(new { Limit = limit, Sample = sample, Units = input.Units.Count, CaptureMs = watch.Elapsed.TotalMilliseconds,
            AllocatedBytes = allocated, DocumentationCaptureMetrics.SourceReads, DocumentationCaptureMetrics.SyntaxParses, UnitsSha256 = hash });
        Console.WriteLine($"limit={limit} sample={sample} units={input.Units.Count} capture={watch.Elapsed.TotalMilliseconds:F1}ms reads={DocumentationCaptureMetrics.SourceReads} parses={DocumentationCaptureMetrics.SyntaxParses}");
    }
}
File.WriteAllText(args[3], JsonSerializer.Serialize(new
{
    SourceHashes = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(args[4])),
    ModelInvocations = 0,
    Samples = samples
}, json));

static void Git(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    process.WaitForExit(); Task.WaitAll(output, error);
    if (process.ExitCode != 0) throw new InvalidOperationException(output.Result + error.Result);
}
