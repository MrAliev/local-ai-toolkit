using System.Globalization;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Broker.Client;
using LocalAi.Contracts;
using LocalLm.Core;

var model = args[0];
var prompt = await File.ReadAllTextAsync(args[1]);
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAi");
using var pointer = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"bin","current.json")));
var version = pointer.RootElement.GetProperty("version").GetString()!;
var executable = Path.Combine(root,"bin","versions",version,"LocalAi.Broker.exe");
var process = new BrokerProcess(executable,root);
var client = new BrokerLocalModelClient(new BrokerClient(new DurableQueue(root),process,observer:new Progress()));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var result = await client.ChatAsync(model,prompt,"Emit only SVG XML that follows the supplied graph exactly.",null,LocalJobPriority.Foreground,cancellation.Token);
await File.WriteAllTextAsync(args[2],JsonSerializer.Serialize(result,LocalAiJson.Strict));
Console.WriteLine(FormattableString.Invariant($"Locally: ChatAsync ({result.Receipt.Model}), execution {result.Receipt.ExecutionDuration.TotalSeconds:F1}s, queue {result.Receipt.QueueDuration.TotalSeconds:F1}s. Cloud token saving not estimated."));
if(result.Receipt.Model != model) throw new InvalidOperationException("The returned model differs from the requested model.");

sealed class Progress : ILocalRunObserver
{
    private DateTimeOffset last = DateTimeOffset.UtcNow;
    public void Report(LocalRunStep step)
    {
        var now = DateTimeOffset.UtcNow;
        if(now-last < TimeSpan.FromSeconds(30)) return;
        last=now;
        Console.WriteLine($"Broker progress: {step}");
    }
}