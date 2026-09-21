using System.Diagnostics;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

internal static class QueueContentionProbe
{
    public static async Task Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("concurrency|enqueue-child|lease-child <root> <output-or-worker>");
        var root = Path.GetFullPath(args[1]);
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = guard.Token;
        if (args[0] != "concurrency")
        {
            var queue = new DurableQueue(Path.Combine(root, "runtime"));
            var worker = args[2];
            File.WriteAllText(Path.Combine(root, args[0] + "-ready-" + worker), "ready");
            while (!File.Exists(Path.Combine(root, args[0] + "-start"))) await Task.Delay(10, ct);
            if (args[0] == "enqueue-child")
            {
                for (var i = 0; i < 20; i++)
                    await queue.EnqueueAsync(LocalJobRequestFactory.CreateEmbed(
                        i < 10 ? "shared-" + i : "worker-" + worker + "-" + i,
                        LocalJobPriority.Foreground, "never-executed-probe-model", ["input"]), ct);
            }
            else
            {
                var lease = await queue.LeaseNextAsync(worker, ct);
                File.WriteAllText(Path.Combine(root, "lease-result-" + worker + ".json"),
                    JsonSerializer.Serialize(new { Acquired = lease is not null, lease?.Request.JobId }));
            }
            return;
        }

        if (Directory.Exists(root)) throw new InvalidOperationException("Concurrency fixture must be new.");
        Directory.CreateDirectory(root);
        await Phase("enqueue-child");
        var parentQueue = new DurableQueue(Path.Combine(root, "runtime"));
        var queued = await parentQueue.ListQueuedAsync(ct);
        if (queued.Count != 50 || queued.Select(value => value.Request.DeduplicationKey).Distinct().Count() != 50 ||
            !queued.Select(value => value.Sequence).Order().SequenceEqual(Enumerable.Range(1, 50).Select(i => (long)i)))
            throw new InvalidOperationException("Cross-process enqueue/deduplication/sequence check failed.");
        await Phase("lease-child");
        var acquired = Enumerable.Range(0, 4).Count(worker => JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "lease-result-" + worker + ".json")))
            .RootElement.GetProperty("Acquired").GetBoolean());
        var remaining = (await parentQueue.ListQueuedAsync(ct)).Count;
        if (acquired != 1 || remaining != 49)
            throw new InvalidOperationException("More than one process leased a job, or queued work was lost.");
        File.WriteAllText(args[2], JsonSerializer.Serialize(new
        {
            ProcessesPerPhase = 4, EnqueueCalls = 80, UniqueJobs = queued.Count,
            Sequences = "1..50 without duplicates", ActiveLeases = acquired, QueuedAfterLease = remaining,
            ModelInvocations = 0
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Phase(string mode)
        {
            var children = new List<(Process Process, Task<string> Error)>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                    };
                    foreach (var argument in new[] { mode, root, i.ToString() }) start.ArgumentList.Add(argument);
                    var process = Process.Start(start)!;
                    children.Add((process, process.StandardError.ReadToEndAsync(ct)));
                }
                while (Enumerable.Range(0, 4).Any(i => !File.Exists(Path.Combine(root, mode + "-ready-" + i))))
                {
                    if (children.Any(child => child.Process.HasExited)) throw new InvalidOperationException("Child exited before the gate.");
                    await Task.Delay(10, ct);
                }
                File.WriteAllText(Path.Combine(root, mode + "-start"), "start");
                await Task.WhenAll(children.Select(child => child.Process.WaitForExitAsync(ct)));
                foreach (var child in children)
                    if (child.Process.ExitCode != 0) throw new InvalidOperationException(await child.Error);
            }
            finally
            {
                foreach (var child in children)
                {
                    if (!child.Process.HasExited) child.Process.Kill(entireProcessTree: true);
                    child.Process.Dispose();
                }
            }
        }
    }
}
