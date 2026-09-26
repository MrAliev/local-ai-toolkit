using System.Text;
using LocalAi.Contracts.Localization;
using LocalLm.Core;
using LocalLm.Mcp;
using LocalAi.Broker.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LocalAi.Contracts;
using LocalAi.Mcp;

// The language every line below is written in, decided before the first one is. Numbers stay
// invariant whatever the language is; only the words move.
OutputCulture.Apply();

// stdio transport: stdout carries JSON-RPC frames only, so all logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

ConsoleOutputText.UseUtf8();

builder.Services.AddLocalLm(new BrokerLocalModelClient(BrokerClientFactory.CreateDefault()));

builder.Services
    .AddMcpServer()
    .WithUsageReporting("locallm", ModelResidencyPolicyStore.DefaultRuntimeRoot)
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
