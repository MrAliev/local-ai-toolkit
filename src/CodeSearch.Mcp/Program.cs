using System.Text;
using CodeSearch.Mcp;
using CodeSearch.Core.Search;
using LocalAi.Contracts.Localization;
using CodeSearch.Core.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LocalAi.Contracts;

// The language every line below is written in, decided before the first one is. Numbers stay
// invariant whatever the language is; only the words move.
OutputCulture.Apply();

// stdio transport means stdout carries JSON-RPC frames and nothing else. Any stray Console.Write
// corrupts the protocol, so logging is pinned to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

ConsoleOutputText.UseUtf8();

builder.Services.AddSingleton(new SearchService());
builder.Services.AddSingleton(new LanguageServerPolicyStore(
    LanguageServerPolicyStore.DefaultRuntimeRoot));
builder.Services.AddSingleton(new HeuristicNavigationPolicyStore(
    HeuristicNavigationPolicyStore.DefaultRuntimeRoot));
builder.Services.AddSingleton(serviceProvider =>
{
    var policyStore = serviceProvider.GetRequiredService<LanguageServerPolicyStore>();
    return new LanguageServerSessionManager((workspaceRoot, languageId) =>
        StdioLanguageServerClient.Start(
            workspaceRoot,
            policyStore.Read().ProcessSpec(languageId)));
});
// One reader for the whole server, and one notice over it.
//
// The reader's runtime root comes from the search service rather than from the machine's default,
// so the figures above an answer and the index that answered cannot come from two installations.
// `index_status` takes the reader directly: a status reads the world and says none of the
// once-only sentences, and asking the notice for them is what spends them.
builder.Services.AddSingleton(serviceProvider => new RepositoryProgressReader(
    serviceProvider.GetRequiredService<SearchService>().RuntimeRoot));
builder.Services.AddSingleton(serviceProvider => new SyncNotice(
    serviceProvider.GetRequiredService<RepositoryProgressReader>().Look));
builder.Services.AddSingleton<ILiveSemanticNavigation, LiveSemanticNavigationOverlay>();
builder.Services.AddSingleton<IHeuristicSemanticNavigation, HeuristicSemanticNavigation>();
builder.Services.AddSingleton<SemanticNavigationGateway>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
