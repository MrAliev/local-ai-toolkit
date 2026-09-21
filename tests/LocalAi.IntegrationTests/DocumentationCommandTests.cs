using System.Text.Json;
using System.Xml.Linq;
using LocalAi.Cli;
using LocalAi.Documentation;

namespace LocalAi.IntegrationTests;

public sealed class DocumentationCommandTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("apply")]
    [InlineData("build --root ignored --limit 0")]
    [InlineData("build --root ignored --limit 2 --all")]
    [InlineData("apply --root ignored --all")]
    [InlineData("apply --root ignored --standalone-html")]
    [InlineData("build --root ignored --standalone-html --standalone-html")]
    [InlineData("build --root ignored --unknown")]
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
