using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CodeSearch.Cli;
using CodeSearch.Core.Semantics;
using CodeSearch.Mcp;
using CodeSearch.Mcp.Resources;
using LocalAi.Tests.Shared;
using ModelContextProtocol.Server;

namespace CodeSearch.Tests;

/// <summary>
/// What the tool list and the printed lines tell an agent, checked against the code that decides
/// it. Each row pins the claim that was false, not the sentence around it.
/// </summary>
public sealed class ToolTextsSayWhatTheCodeDoesTests
{
    [Fact]
    public void A_search_root_defaults_to_the_worktree_rather_than_the_main_repository()
    {
        var root = Flat(Parameter("search_code", "root"));

        Assert.Contains("Defaults to the worktree containing the working directory", root, StringComparison.Ordinal);
        Assert.DoesNotContain("resolves to its main repository", root, StringComparison.Ordinal);
    }

    /// <summary>Symbol chunking follows the definitions an indexer reported, not the language.</summary>
    [Fact]
    public void The_search_description_names_the_indexers_that_decide_symbol_chunking()
    {
        var description = Flat(Tool("search_code"));

        Assert.Contains(SemanticIndexingPolicy.Default.TypeScript.Executable, description, StringComparison.Ordinal);
        Assert.Contains(SemanticIndexingPolicy.Default.Python.Executable, description, StringComparison.Ordinal);
        Assert.DoesNotContain("C#, TypeScript and Python are chunked by symbol", description, StringComparison.Ordinal);
        Assert.Contains("indexed only in a file that declares no type", description, StringComparison.Ordinal);
    }

    /// <summary>The commit a drifted index is compared with is the tip of the manifest's mainline.</summary>
    [Fact]
    public void Drift_is_described_against_the_mainline_rather_than_head()
    {
        Assert.DoesNotContain("HEAD", Tool("index_status"), StringComparison.Ordinal);
        Assert.Contains("mainline branch", Flat(Tool("index_status")), StringComparison.Ordinal);
        foreach (var line in new[]
                 {
                     CodeSearchText.StatusStale("a1b2c3d", "f9e8d7c"),
                     CodeSearchText.SearchIndexHeaderStale("a1b2c3d", "f9e8d7c"),
                 })
        {
            Assert.DoesNotContain("HEAD", line, StringComparison.Ordinal);
            Assert.Contains("mainline branch", line, StringComparison.Ordinal);
        }

        using var reading = TestCulture.Reading("ru");
        foreach (var line in new[]
                 {
                     CodeSearchText.StatusStale("a1b2c3d", "f9e8d7c"),
                     CodeSearchText.SearchIndexHeaderStale("a1b2c3d", "f9e8d7c"),
                 })
        {
            Assert.DoesNotContain("HEAD", line, StringComparison.Ordinal);
            Assert.Contains("основная ветка", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_refresh_bound_is_stated_in_files_as_the_code_counts_it()
    {
        var limit = (int)typeof(CodeSearchTools)
            .GetField("InlineRefreshFileLimit", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Contains($"more than {limit} files to re-read", Flat(Tool("index_refresh")), StringComparison.Ordinal);
    }

    /// <summary>Past the ceiling the explanation is not trimmed: the whole call is refused.</summary>
    [Fact]
    public void An_explanation_past_the_evidence_ceiling_is_described_as_a_refusal()
    {
        var ceiling = SemanticImpactExplanation.MaximumEvidence;
        var description = Flat(Tool("analyze_impact"));
        var explain = Flat(Parameter("analyze_impact", "explain"));

        Assert.Contains($"{ceiling} or fewer returned items", description, StringComparison.Ordinal);
        Assert.Contains("refused and returns no graph", description, StringComparison.Ordinal);
        Assert.Contains("impact_explanation_limit", explain, StringComparison.Ordinal);
        Assert.Contains($"more than {ceiling} items", explain, StringComparison.Ordinal);
    }

    /// <summary>`codesearch status` prints the base index path and no overlay path unless one is missing.</summary>
    [Fact]
    public void The_usage_promises_only_the_path_status_prints()
    {
        Assert.DoesNotContain("Their real paths", CodeSearchUsage.Text, StringComparison.Ordinal);
        Assert.Contains("Where the base index really lives", Flat(CodeSearchUsage.Text), StringComparison.Ordinal);

        using var reading = TestCulture.Reading("ru");
        Assert.DoesNotContain("пути индексов", CodeSearchUsage.Text, StringComparison.Ordinal);
        Assert.Contains("путь базового индекса", Flat(CodeSearchUsage.Text), StringComparison.Ordinal);
    }

    /// <summary>`capabilities` takes --json without brackets, because there it is required.</summary>
    [Fact]
    public async Task The_json_refusal_does_not_promise_brackets_the_usage_does_not_print()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Assembly.Load("codesearch").Location);
        start.ArgumentList.Add("index");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        _ = await error;
        var refusal = await output;

        Assert.Contains("json_not_supported", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("[--json]", refusal, StringComparison.Ordinal);
        Assert.Contains("--json on every command that takes it", refusal, StringComparison.Ordinal);
    }

    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static MethodInfo Method(string tool) => typeof(CodeSearchTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);

    private static string Tool(string tool) =>
        Method(tool).GetCustomAttribute<DescriptionAttribute>()!.Description;

    private static string Parameter(string tool, string parameter) =>
        Method(tool).GetParameters().Single(candidate => candidate.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
}
