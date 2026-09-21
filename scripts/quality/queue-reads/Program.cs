using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

if (args.Length > 0 && args[0] is "concurrency" or "enqueue-child" or "lease-child")
{
    await QueueContentionProbe.Run(args);
    return;
}
if (args.Length != 3) throw new ArgumentException("<new-runtime-parent> <output.json> <unmodified-source-sha256>");
var parent = Path.GetFullPath(args[0]);
if (Directory.Exists(parent)) throw new InvalidOperationException("Runtime parent must be new.");
Directory.CreateDirectory(parent);
var samples = new List<object>();
var imageBytes = new byte[1024 * 1024];
new Random(621).NextBytes(imageBytes);
var image = Convert.ToBase64String(imageBytes);
var sourceHash = args[2];
var options = new JsonSerializerOptions { WriteIndented = true };
var report = new
{
    SourceSha256 = sourceHash,
    AssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DurableQueue).Assembly.Location))),
    Environment.Version, Environment.OSVersion, ImageBytes = imageBytes.Length,
    ModelInvocations = 0, Samples = samples
};
foreach (var kind in new[] { "text", "image" })
foreach (var count in new[] { 1, 10, 100 })
{
    var root = Path.Combine(parent, kind + "-" + count);
    var queue = new DurableQueue(root);
    var requests = new Dictionary<Guid, LocalJobRequest>();
    for (var i = 0; i < count; i++)
    {
        var request = LocalJobRequestFactory.CreateNativeOllama("probe-" + i,
            LocalJobPriority.Interactive, NativeOllamaOperation.Generate,
            JsonSerializer.SerializeToElement(new
            {
                model = "never-executed-probe-model", prompt = "Read the supplied content " + i,
                images = kind == "image" ? new[] { image } : Array.Empty<string>()
            }), Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"),
            new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        queue.EnqueueAsync(request).GetAwaiter().GetResult();
        requests.Add(request.JobId, request);
        if ((i + 1) % 10 == 0 || count == 1) Console.WriteLine($"Preparing {kind}: {i + 1}/{count}");
    }
    var directories = Directory.EnumerateDirectories(Path.Combine(root, "jobs")).ToArray();
    QueueReadProbeMetrics.RequestLengths.Clear();
    foreach (var directory in directories)
        QueueReadProbeMetrics.RequestLengths.Add(directory, new FileInfo(Path.Combine(directory, "request.json")).Length);
    // Match the final entry in the same filesystem enumeration so Enqueue scans every key.
    var duplicate = requests[Guid.ParseExact(Path.GetFileName(directories[^1]), "N")];
    foreach (var operation in new[] { "list", "enqueue-join" })
    {
        object Execute() => operation == "list"
            ? queue.ListQueuedAsync().GetAwaiter().GetResult()
            : queue.EnqueueAsync(duplicate).GetAwaiter().GetResult();
        _ = Execute();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            QueueReadProbeMetrics.Reset();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var result = Execute();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (result is IReadOnlyList<QueuedJobCandidate> candidates && candidates.Count != count)
                throw new InvalidOperationException("Queue lost candidates.");
            if (result is EnqueueResult join && (!join.JoinedExisting || join.JobId != duplicate.JobId))
                throw new InvalidOperationException("Deduplication changed.");
            samples.Add(new
            {
                Kind = kind, Jobs = count, Operation = operation, Iteration = iteration,
                ElapsedMs = elapsed,
                MutexHeldMs = QueueReadProbeMetrics.HeldTicks * 1000.0 / Stopwatch.Frequency,
                AllocatedBytes = allocated, QueueReadProbeMetrics.RequestReads, QueueReadProbeMetrics.RequestBytes
            });
            File.WriteAllText(args[1], JsonSerializer.Serialize(report, options));
            Console.WriteLine($"{kind}/{count}/{operation}/{iteration}: {elapsed:F2} ms, {allocated} bytes, {QueueReadProbeMetrics.RequestReads} reads");
            GC.KeepAlive(result);
        }
    }
}
