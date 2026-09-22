using System.Text.Json;
using System.Xml.Linq;
using LocalAi.Cli;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationCommandTests
{
    [Fact]
    public async Task A_path_plan_lists_required_sources_without_generating_a_package()
    {
        using var repo = new DocumentationRepositoryFixture(source: "public class A : B {}", relationship: true,
            extraSources: new Dictionary<string, string> { ["lib/B.cs"] = "public class B {}" });
        var output = new StringWriter(); var error = new StringWriter(); var calls = 0;
        var exit = await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root, "--path", "src/Types.cs", "--plan"],
            new(output, error), true, TestContext.Current.CancellationToken, repo.Runtime,
            (_, _) => { calls++; throw new InvalidOperationException(); });
        Assert.Equal(0, exit);
        Assert.Equal(0, calls);
        using var response = JsonDocument.Parse(output.ToString());
        var plan = response.RootElement.GetProperty("data");
        Assert.Equal("src/Types.cs", plan.GetProperty("path").GetString());
        var unit = Assert.Single(plan.GetProperty("units").EnumerateArray());
        Assert.Equal("A", unit.GetProperty("symbolId").GetString());
        Assert.Equal("src/Types.cs", Assert.Single(unit.GetProperty("sourcePaths").EnumerateArray()).GetString());
        Assert.Equal("lib/B.cs", Assert.Single(unit.GetProperty("dependencyPaths").EnumerateArray()).GetString());
        Assert.False(Directory.Exists(Path.Combine(repo.Identity.RepositoryRuntimeRoot.Value, "documentation")));
    }

    [Fact]
    public async Task Scoped_build_and_apply_preserve_manual_edits_and_do_not_generate_unselected_types()
    {
        using var repo = new DocumentationRepositoryFixture(extraSources: new Dictionary<string, string>
        { ["other/B.cs"] = "public class B {}" });
        var output = new StringWriter(); var error = new StringWriter();
        var generated = new List<string>();
        Task<DocumentationDraft> Generate(DocumentationInput input, CancellationToken _)
        { generated.Add(input.SymbolId); return Task.FromResult(Draft(input)); }
        async Task Run(string operation)
        {
            var args = operation == "build" ? new[] { "build", "--root", repo.Root, "--path", "src/Types.cs" }
                : new[] { "apply", "--root", repo.Root };
            Assert.Equal(0, await DocumentationCommand.ExecuteAsync(args, new(output, error), false,
                TestContext.Current.CancellationToken, repo.Runtime, Generate));
        }
        await Run("build"); await Run("apply");
        var document = Assert.Single(Directory.GetFiles(repo.Root, "page.en.md", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(document, "manual editorial change", TestContext.Current.CancellationToken);
        await Run("build"); await Run("apply");
        Assert.Equal(["A"], generated);
        Assert.Equal("manual editorial change", await File.ReadAllTextAsync(document, TestContext.Current.CancellationToken));
        Assert.Contains("src/Types.cs", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_path_scope_is_an_explicit_refusal_without_generation()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter(); var calls = 0;
        var exit = await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root, "--path", "missing", "--plan"],
            new(output, new StringWriter()), true, TestContext.Current.CancellationToken, repo.Runtime,
            (_, _) => { calls++; throw new InvalidOperationException(); });
        Assert.Equal(65, exit); Assert.Equal(0, calls);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("scope_empty", response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancelled_path_planning_does_not_publish_a_package()
    {
        using var repo = new DocumentationRepositoryFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentationCommand.ExecuteAsync(
            ["build", "--root", repo.Root, "--path", "src", "--plan"], new(new StringWriter(), new StringWriter()), true,
            cancellation.Token, repo.Runtime, (_, _) => throw new InvalidOperationException("must not generate")));
        Assert.False(Directory.Exists(Path.Combine(repo.Identity.RepositoryRuntimeRoot.Value, "documentation")));
    }

    [Fact]
    public async Task Prune_defaults_to_a_read_only_preview_without_model_calls()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter(); var error = new StringWriter(); var calls = 0;
        var exit = await DocumentationCommand.ExecuteAsync(["prune", "--root", repo.Root], new(output, error), true,
            TestContext.Current.CancellationToken, repo.Runtime, (_, _) => { calls++; throw new InvalidOperationException(); });
        Assert.Equal(0, exit);
        Assert.Equal(0, calls);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.True(response.RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Empty(response.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray());
    }

    [Theory]
    [InlineData("build")]
    [InlineData("apply")]
    [InlineData("build --root ignored --limit 0")]
    [InlineData("build --root ignored --limit 2 --all")]
    [InlineData("apply --root ignored --all")]
    [InlineData("apply --root ignored --standalone-html")]
    [InlineData("build --root ignored --standalone-html --standalone-html")]
    [InlineData("build --root ignored --unknown")]
    [InlineData("build --root ignored --path")]
    [InlineData("build --root ignored --path ../outside")]
    [InlineData("build --root ignored --path src --path other")]
    [InlineData("build --root ignored --plan --plan")]
    [InlineData("apply --root ignored --path src")]
    [InlineData("apply --root ignored --plan")]
    [InlineData("prune --root ignored --keep 0")]
    [InlineData("prune --root ignored --keep -1")]
    [InlineData("prune --root ignored --apply --dry-run")]
    [InlineData("prune --root ignored --apply --apply")]
    [InlineData("prune --root ignored --all")]
    [InlineData("pin --root ignored")]
    [InlineData("pin --root ignored --package ../outside")]
    [InlineData("unpin --root ignored --package invalid")]
    public async Task Invalid_arguments_are_refused_before_any_model_call(string arguments)
    {
        var output = new StringWriter(); var error = new StringWriter(); var calls = 0;
        var exit = await DocumentationCommand.ExecuteAsync(arguments.Split(' '), new(output, error), true, TestContext.Current.CancellationToken,
            generate: (_, _) => { calls++; throw new InvalidOperationException(); });
        Assert.Equal(2, exit); Assert.Equal(0, calls);
        using var answer = JsonDocument.Parse(output.ToString());
        Assert.False(answer.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Build_reuse_and_apply_run_through_the_command_without_apply_calling_the_model()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter(); var error = new StringWriter(); var calls = 0;
        Task<DocumentationDraft> Generate(DocumentationInput input, CancellationToken _) { calls++; return Task.FromResult(Draft(input)); }
        var first = await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root], new(output, error), false, TestContext.Current.CancellationToken, repo.Runtime, Generate);
        var second = await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root], new(output, error), false, TestContext.Current.CancellationToken, repo.Runtime, Generate);
        Assert.Equal(0, first); Assert.Equal(0, second); Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(repo.Root, "page.*", SearchOption.AllDirectories));
        var applied = await DocumentationCommand.ExecuteAsync(["apply", "--root", repo.Root], new(output, error), false, TestContext.Current.CancellationToken, repo.Runtime, Generate);
        Assert.Equal(0, applied); Assert.Equal(1, calls);
        Assert.Single(Directory.GetFiles(repo.Root, "page.ru.pdf", SearchOption.AllDirectories));
        Assert.Contains("docs", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Commands_are_discoverable_with_their_machine_output_shape()
    {
        Assert.Contains("docs build", MachineOutput.Commands);
        Assert.Contains("docs apply", MachineOutput.Commands);
        Assert.Contains("localai docs build", CliUsage.Text);
        Assert.Contains("--standalone-html", CliUsage.Text);
        Assert.Contains("--path", CliUsage.Text);
        Assert.Contains("--plan", CliUsage.Text);
        foreach (var operation in new[] { "prune", "pin", "unpin" })
        {
            Assert.Contains("docs " + operation, MachineOutput.Commands);
            Assert.Contains("localai docs " + operation, CliUsage.Text);
        }
    }

    [Fact]
    public async Task Standalone_html_flag_reuses_text_and_applies_a_self_contained_page()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter(); var error = new StringWriter(); var calls = 0;
        Task<DocumentationDraft> Generate(DocumentationInput input, CancellationToken _) { calls++; return Task.FromResult(Draft(input)); }
        Assert.Equal(0, await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root], new(output, error), false,
            TestContext.Current.CancellationToken, repo.Runtime, Generate));
        Assert.Equal(0, await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root, "--standalone-html"], new(output, error), false,
            TestContext.Current.CancellationToken, repo.Runtime, Generate));
        Assert.Equal(1, calls);
        Assert.Equal(0, await DocumentationCommand.ExecuteAsync(["apply", "--root", repo.Root], new(output, error), false,
            TestContext.Current.CancellationToken, repo.Runtime));
        var html = File.ReadAllText(Assert.Single(Directory.GetFiles(repo.Root, "page.en.html", SearchOption.AllDirectories)));
        Assert.Contains("data:font/ttf;base64,", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.Empty(Directory.GetFiles(repo.Root, "*.css", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Json_build_and_apply_use_explicit_camel_case_contracts()
    {
        using var repo = new DocumentationRepositoryFixture();
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await DocumentationCommand.ExecuteAsync(["build", "--root", repo.Root], new(output, error), true,
            TestContext.Current.CancellationToken, repo.Runtime, (input, _) => Task.FromResult(Draft(input))));
        using (var response = JsonDocument.Parse(output.ToString()))
        {
            var data = response.RootElement.GetProperty("data");
            Assert.Equal(0, data.GetProperty("failed").GetInt32());
            Assert.Equal(0, data.GetProperty("reused").GetInt32());
            Assert.NotEmpty(data.GetProperty("packagePath").GetString()!);
            Assert.Equal("A", data.GetProperty("units")[0].GetProperty("symbolId").GetString());
        }
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await DocumentationCommand.ExecuteAsync(["apply", "--root", repo.Root], new(output, error), true,
            TestContext.Current.CancellationToken, repo.Runtime));
        using var applied = JsonDocument.Parse(output.ToString());
        Assert.Equal(10, applied.RootElement.GetProperty("data").GetProperty("copied").GetInt32());
        Assert.Empty(applied.RootElement.GetProperty("data").GetProperty("skipped").EnumerateArray());
    }

    internal static DocumentationDraft Draft(DocumentationInput input)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var svg = new XElement(ns + "svg", new XAttribute("width", 800), new XAttribute("height", 300),
            input.Nodes.Select(node => new XElement(ns + "g", new XAttribute("data-symbol-id", node.Id), new XElement(ns + "text", node.Label))),
            input.Edges.Select(edge => new XElement(ns + "line", new XAttribute("data-from", edge.From), new XAttribute("data-to", edge.To))));
        return new([new([input.Facts[0].Id], "An indexed type.", "Проиндексированный тип.")], [], svg.ToString());
    }
}
