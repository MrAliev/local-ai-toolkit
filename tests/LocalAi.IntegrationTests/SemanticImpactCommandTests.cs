using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class SemanticImpactCommandTests
{
    [Theory]
    [InlineData("--path src/A.cs --line 1 --limit 0")]
    [InlineData("--path src/A.cs --line 1 --limit 10001")]
    [InlineData("--path src/A.cs --line 0")]
    [InlineData("--path src/A.cs --line 1 --column 0")]
    [InlineData("--path ../A.cs --line 1")]
    [InlineData("--path src/A.cs --path src/B.cs --line 1")]
    [InlineData("--path src/A.cs --line 1 --surprise")]
    [InlineData("--path src/A.cs --line")]
    public void Invalid_arguments_return_a_machine_refusal(string arguments)
    {
        var output = new StringWriter();
        Assert.Equal(2, SemanticImpactCommand.Execute(arguments.Split(' '), new(output, new StringWriter()), true,
            TestContext.Current.CancellationToken, "unused"));
        using var response = JsonDocument.Parse(output.ToString());
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("semantic impact", response.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Impact_is_discoverable_in_help_capabilities_and_usage_policy()
    {
        Assert.Contains("semantic impact", MachineOutput.Commands);
        Assert.Contains("--limit", CliHelp.Render("localai", "semantic impact"));
        Assert.Equal("analyze_impact", UsageReportingRegistry.Find(UsageSurface.Cli, "localai", "semantic impact").McpTool);
    }

    [Fact]
    public void Cancelled_impact_does_not_load_or_write_a_result()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new StringWriter();
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticImpactCommand.Execute([], new(output, new StringWriter()), true,
            cancellation.Token, "unused"));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Changed_sources_refuse_until_the_index_is_refreshed()
    {
        using var repo = new DocumentationRepositoryFixture();
        File.AppendAllText(Path.Combine(repo.Root, "src", "Types.cs"), "\npublic class B {}\n");
        var output = new StringWriter();
        Assert.Equal(65, SemanticImpactCommand.Execute(["--root", repo.Root, "--path", "src/Types.cs", "--line", "1"],
            new(output, new StringWriter()), true, TestContext.Current.CancellationToken, repo.Runtime));
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("semantic_navigation_not_ready", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Impact_command_returns_a_source_checkable_one_based_snapshot()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter();
        var exit = SemanticImpactCommand.Execute(["--root", repo.Root, "--path", "src/Types.cs", "--line", "1"],
            new(output, new StringWriter()), true, TestContext.Current.CancellationToken, repo.Runtime);
        Assert.Equal(0, exit);
        using var response = JsonDocument.Parse(output.ToString());
        var data = response.RootElement.GetProperty("data");
        Assert.Equal("direct-indexed-csharp", data.GetProperty("scope").GetString());
        var definition = Assert.Single(data.GetProperty("definitions").EnumerateArray());
        Assert.Equal(1, definition.GetProperty("startLine").GetInt32());
        Assert.Equal(14, definition.GetProperty("startColumn").GetInt32());
        Assert.Equal("src/Types.cs", definition.GetProperty("path").GetString());
        Assert.Equal(64, definition.GetProperty("documentSha256").GetString()!.Length);
        Assert.Equal(repo.Identity.HeadTree, data.GetProperty("snapshot").GetProperty("gitTree").GetString());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }
}
