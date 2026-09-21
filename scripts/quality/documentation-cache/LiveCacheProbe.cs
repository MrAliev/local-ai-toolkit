#if CACHE_SPLIT
using System.Diagnostics;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Broker;
using LocalAi.Contracts;
using LocalAi.Documentation;
using LocalLm.Core;

internal static class LiveCacheProbe
{
    internal static async Task RunAsync(string root, string brokerRuntime, string output, string brokerExecutable)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Live probe root must be new.");
        Directory.CreateDirectory(root);
        var receipts = new List<object>();
        var currentTypes = 0; var currentPhase = "";
        new RuntimeAcl().Ensure(brokerRuntime);
        var client = new BrokerClient(new DurableQueue(brokerRuntime), new BrokerProcess(brokerExecutable, brokerRuntime), timeout: null, observer: null);
        var model = new CountingModel(new BrokerDocumentationModel(new BrokerLocalModelClient(client), receipt =>
        {
            var shortfall = receipt.Routing?.ResidencyShortfall.ToString() ?? "offload_unknown";
            receipts.Add(new { Types = currentTypes, Phase = currentPhase, receipt.Model, QueueMs = receipt.QueueDuration.TotalMilliseconds, ExecutionMs = receipt.ExecutionDuration.TotalMilliseconds, Shortfall = shortfall, receipt.Routing?.VramResidentPercent });
            Console.WriteLine($"model={receipt.Model} shortfall={shortfall} queue={receipt.QueueDuration.TotalSeconds:F3}s execution={receipt.ExecutionDuration.TotalSeconds:F3}s cloud-token-comparison=not-measured");
        }));
        var generator = new DocumentationGenerator(model);
        var versions = DocumentationCacheVersions.ForRuntime(brokerRuntime);
        var samples = new List<object>();
        foreach (var count in new[] { 1, 5, 12 })
        {
            currentTypes = count;
            var fixture = Path.Combine(root, "types-" + count);
            var worktree = Path.Combine(fixture, "worktree"); Directory.CreateDirectory(worktree);
            var cacheRuntime = Path.Combine(fixture, "cache");
            var units = Enumerable.Range(0, count).Select(ProbeData.Unit).ToArray();
            var store = new DocumentationPackStore(cacheRuntime, ProbeData.RepositoryId, "live-probe", versions);
            await Step("cold");
            units = units.Select(unit => unit with { Content = unit.Content with { SourceLine = 47, Snapshot = "snapshot-2" } }).ToArray();
            await Step("presentation");
            store = new DocumentationPackStore(cacheRuntime, ProbeData.RepositoryId, "live-probe", versions with { Renderer = "live-renderer-next" });
            await Step("renderer");

            async Task Step(string phase)
            {
                currentPhase = phase;
                DocumentationCacheMetrics.Reset();
                var before = model.Requests;
                var generationTicks = 0L; var generatorValidationTicks = 0L; var generationCalls = 0;
                var clock = Stopwatch.StartNew();
                var report = await store.BuildAsync(new(worktree, "fixture", units), async (input, token) =>
                {
                    generationCalls++;
                    var started = Stopwatch.GetTimestamp(); var validationBefore = DocumentationCacheMetrics.ValidationTicks;
                    try { return await generator.GenerateAsync(input, token); }
                    finally
                    {
                        generationTicks += Stopwatch.GetTimestamp() - started;
                        generatorValidationTicks += DocumentationCacheMetrics.ValidationTicks - validationBefore;
                    }
                }, progress => Console.WriteLine($"types={count} phase={phase} symbol={progress.Name} stage={progress.Phase}"));
                clock.Stop();
                var requests = model.Requests - before;
                var fresh = report.Failed == 0 && report.Units.Count == units.Length;
                if (fresh)
                    foreach (var (result, i) in report.Units.Select((result, i) => (result, i)))
                    {
                        var artifact = Path.Combine(cacheRuntime, "repositories", ProbeData.RepositoryId, "documentation", "artifacts", result.ArtifactId!, "page.en.md");
                        var page = File.ReadAllText(artifact);
                        fresh &= page.Contains(units[i].Content.SourcePath + ":" + units[i].Content.SourceLine, StringComparison.Ordinal) && page.Contains(units[i].Content.Snapshot, StringComparison.Ordinal);
                    }
                var routeUnchanged = DocumentationCacheVersions.ForRuntime(brokerRuntime).Route == versions.Route;
                samples.Add(new { Types = count, Phase = phase, BuildMs = clock.Elapsed.TotalMilliseconds, ModelRequests = requests,
                    GenerationCalls = generationCalls, GenerationMs = (generationTicks - generatorValidationTicks) * 1000.0 / Stopwatch.Frequency,
                    ValidationMs = DocumentationCacheMetrics.ValidationTicks * 1000.0 / Stopwatch.Frequency,
                    RenderMs = DocumentationCacheMetrics.RenderTicks * 1000.0 / Stopwatch.Frequency,
                    report.Failed, FreshPresentation = fresh, RouteUnchanged = routeUnchanged,
                    Errors = report.Units.Where(u => u.Error is not null).Select(u => u.Error).ToArray() });
                File.WriteAllText(output, JsonSerializer.Serialize(new { CorpusTypes = 12, BrokerVersionDirectory = Path.GetFileName(Path.GetDirectoryName(brokerExecutable)), Samples = samples, Receipts = receipts, CloudTokenSavings = (int?)null, TokenReason = "No cloud comparison; model request counts and timing are separate diagnostics" }, new JsonSerializerOptions { WriteIndented = true }));
                if (report.Failed != 0 || !fresh || !routeUnchanged || phase != "cold" && requests != 0) throw new InvalidOperationException(JsonSerializer.Serialize(report.Units));
            }
        }
    }

    private sealed class CountingModel(IDocumentationModel inner) : IDocumentationModel
    {
        internal int Requests;
        public Task<string> CompleteAsync(string model, string prompt, CancellationToken cancellationToken)
        { Requests++; return inner.CompleteAsync(model, prompt, cancellationToken); }
        public Task<string> TranslateAsync(string english, CancellationToken cancellationToken)
        { Requests++; return inner.TranslateAsync(english, cancellationToken); }
    }
}
#endif
