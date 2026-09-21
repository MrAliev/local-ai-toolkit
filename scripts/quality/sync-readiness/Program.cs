using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSearch.Core.Embedding;
using CodeSearch.Core.Indexing;
using CodeSearch.Core.Search;
using LocalAi.Broker.Client;
using LocalAi.Cli;
using LocalAi.Contracts;

// All artifacts and the synthetic Git repository belong to the caller's fresh fixture directory.
// Inference still uses the shared broker. Never point this probe at a user's repository/runtime.
if (args.Length != 3 || args[0] is not ("prepare" or "measure"))
    throw new ArgumentException("prepare|measure <fixture-directory> <output-json>");
var fixture = Path.GetFullPath(args[1]);
var root = Path.Combine(fixture, "repo");
var active = Path.Combine(fixture, "active");
var other = Path.Combine(fixture, "other");
var runtime = Path.Combine(fixture, "runtime");
var statePath = Path.Combine(fixture, "probe-state.json");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var ct = deadline.Token;
if (args[0] == "prepare")
{
    if (Directory.Exists(fixture)) throw new InvalidOperationException("The fixture directory must be new.");
    Directory.CreateDirectory(root);
    Git("init", "-b", "main");
    Git("config", "user.email", "probe@local.invalid");
    Git("config", "user.name", "LocalAi Sync Probe");
    WriteCorpus("OldSymbol");
    Git("add", ".");
    Git("commit", "-m", "Old corpus");
    var previous = await CodeSearchSyncCommand.ExecuteAsync(root, Console.Error,
        cancellationToken: ct, includeOverlays: false, runtimeRoot: runtime);
    var previousDirectory = Path.GetDirectoryName(previous.BaseIndexPath)!;
    var backup = Path.Combine(fixture, "previous-generation");
    Directory.CreateDirectory(backup);
    foreach (var file in Directory.EnumerateFiles(previousDirectory))
        File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
    Git("worktree", "add", "-b", "other", other);
    WriteCorpus("NewSymbol");
    Git("add", ".");
    Git("commit", "-m", "New corpus");
    Git("worktree", "add", "-b", "active", active);
    var next = await CodeSearchSyncCommand.ExecuteAsync(root, Console.Error,
        cancellationToken: ct, includeOverlays: false, runtimeRoot: runtime);
    Directory.CreateDirectory(previousDirectory);
    foreach (var file in Directory.EnumerateFiles(backup))
        File.Copy(file, Path.Combine(previousDirectory, Path.GetFileName(file)), overwrite: true);
    var state = new ProbeState(previous.GenerationId, next.GenerationId,
        SourceSnapshot.From(RuntimeIndexLayout.Inspect(active, runtime)));
    File.WriteAllText(statePath, JsonSerializer.Serialize(state, LocalAiJson.Strict));
    File.WriteAllText(args[2], JsonSerializer.Serialize(new
    {
        state.Previous, state.Next, State = state.Identity, Files = 32, LinesPerFile = 128
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var saved = JsonSerializer.Deserialize<ProbeState>(File.ReadAllText(statePath), LocalAiJson.Strict)!;
var identity = RuntimeIndexLayout.Inspect(active, runtime);
if (saved.Identity != SourceSnapshot.From(identity) || identity.DirtyHash is not null)
    throw new InvalidOperationException("The fixture source changed.");
var store = new GenerationStore(identity.RepositoryRuntimeRoot);
// Successful full maintenance may prune the old generation. Restore the identical saved bytes.
var previousPath = Path.GetDirectoryName(store.IndexPath(saved.Previous))!;
Directory.CreateDirectory(previousPath);
foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "previous-generation")))
    File.Copy(file, Path.Combine(previousPath, Path.GetFileName(file)), overwrite: true);
var nextGeneration = store.ReadManifest(saved.Next);
store.SetCurrent(store.ReadManifest(saved.Previous));
// Only recomputable artifacts in this probe's own marked fixture are reset between runs.
DeleteOwned(Path.Combine(identity.RepositoryRuntimeRoot.Value, "overlays", saved.Next));
DeleteOwned(Path.Combine(identity.RepositoryRuntimeRoot.Value, "worktree-publications"));
var modelBefore = await ModelIdentity();
var elapsed = Stopwatch.StartNew();
double? activeReadyMs = null;
double lastNotReadyMs = 0;
double fullMs = 0;
string? globalAtReadiness = null;
CodeSearchSyncResult? result = null;
var sync = Task.Run(async () =>
{
    result = await CodeSearchSyncCommand.ExecuteAsync(active, Console.Error,
        cancellationToken: ct, runtimeRoot: runtime);
    fullMs = elapsed.Elapsed.TotalMilliseconds;
}, ct);
while (true)
{
    var selected = RuntimeIndexLayout.ResolveBaseIndexPath(active, runtime);
    var index = CodeIndex.Load(selected, withVectors: false);
    if (index.GenerationId == saved.Next)
    {
        activeReadyMs = elapsed.Elapsed.TotalMilliseconds;
        globalAtReadiness = store.ReadCurrent()?.GenerationId;
        break;
    }
    lastNotReadyMs = elapsed.Elapsed.TotalMilliseconds;
    if (sync.IsCompleted) { await sync; throw new InvalidOperationException("Active tree never became ready."); }
    await Task.Delay(100, ct);
}
await sync;
if (store.ReadCurrent()?.GenerationId != saved.Next || RuntimeIndexLayout.Inspect(active, runtime) != identity)
    throw new InvalidOperationException("Maintenance did not publish the expected source generation.");
// Query only after the timed sync, so validation cannot contend with overlay embedding.
using var search = new SearchService(model => new BrokerEmbeddingClient(model, BrokerClientFactory.CreateDefault()),
    runtimeRoot: runtime);
var hits = await search.SearchAsync("NewSymbol7", active, new SearchOptions { TopK = 5 }, ct);
if (!hits.Hits.Any(hit => hit.RelPath.Replace('\\', '/') == "document-07.md"))
    throw new InvalidOperationException("The active source was not searchable.");
var oldHits = await search.SearchAsync("OldSymbol7", other, new SearchOptions { TopK = 5 }, ct);
if (!oldHits.Hits.Any(hit => hit.RelPath.Replace('\\', '/') == "document-07.md"))
    throw new InvalidOperationException("The other source was not searchable after maintenance.");
File.WriteAllText(args[2], JsonSerializer.Serialize(new
{
    ActiveReadyMs = activeReadyMs, LastNotReadyMs = lastNotReadyMs, FullMaintenanceMs = fullMs,
    GlobalGenerationAtReadiness = globalAtReadiness, saved.Previous, saved.Next,
    State = SourceSnapshot.From(identity), Files = 32, LinesPerFile = 128,
    BaseChunks = CodeIndex.Load(store.IndexPath(saved.Next), withVectors: false).Chunks.Count,
    Model = nextGeneration.Identity.EmbeddingModel, PollDelayMs = 100, result,
    ModelBefore = modelBefore, ModelAfter = await ModelIdentity(),
    Implementation = Environment.GetEnvironmentVariable("SYNC_PROBE_REVISION") ?? "unrecorded",
    CoreSha256 = Hash(typeof(SearchService).Assembly.Location),
    CliSha256 = Hash(typeof(CodeSearchSyncCommand).Assembly.Location),
    ActiveHits = hits.Hits.Select(hit => new { hit.RelPath, hit.StartLine, hit.EndLine }),
    OtherHits = oldHits.Hits.Select(hit => new { hit.RelPath, hit.StartLine, hit.EndLine })
}, new JsonSerializerOptions { WriteIndented = true }));

static string Hash(string path) => Convert.ToHexStringLower(
    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

async Task<JsonElement> ModelIdentity()
{
    var request = LocalJobRequestFactory.CreateNativeOllama("sync-probe:tags:" + Guid.NewGuid(),
        LocalJobPriority.Interactive, NativeOllamaOperation.Tags, null);
    var response = await BrokerClientFactory.CreateDefault().ExecuteAsync<NativeOllamaJobOutput>(request, ct);
    return response.Value.Response.GetProperty("models").EnumerateArray()
        .Single(model => model.GetProperty("name").GetString() == CodeSearchSyncCommand.DefaultModel).Clone();
}

void WriteCorpus(string symbol)
{
    for (var file = 0; file < 32; file++)
    {
        var text = new StringBuilder();
        for (var line = 0; line < 128; line++)
            text.AppendLine($"{symbol}{file}: document {file}, record {line}; deterministic sync readiness fixture.");
        File.WriteAllText(Path.Combine(root, $"document-{file:D2}.md"), text.ToString());
    }
}

void Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync(ct);
    var stderr = process.StandardError.ReadToEndAsync(ct);
    process.WaitForExit();
    Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
    if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Result);
}

void DeleteOwned(string path)
{
    var full = Path.GetFullPath(path);
    if (!full.StartsWith(Path.GetFullPath(runtime) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to remove a path outside the probe runtime.");
    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
}

sealed record ProbeState(string Previous, string Next, SourceSnapshot Identity);
sealed record SourceSnapshot(string RepositoryId, string HeadCommit, string HeadTree,
    string? DirtyHash, string? SemanticDirtyHash)
{
    public static SourceSnapshot From(WorkingIndexIdentity identity) => new(identity.RepositoryId,
        identity.HeadCommit, identity.HeadTree, identity.DirtyHash, identity.SemanticDirtyHash);
}
