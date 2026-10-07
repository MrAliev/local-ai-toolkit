using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Tests.Shared;

namespace LocalAi.IntegrationTests;

public sealed class DiagnosticCommandTests
{
    [Fact]
    public async Task Save_refusal_preserves_output_and_does_not_echo_private_exception_details()
    {
        using var fixture = new TemporaryDirectory("diagnostic-command");
        var path = fixture.Combine("PRIVATE-path.zip");
        File.WriteAllText(path, "keep");
        var output = new StringWriter();
        var code = await DiagnosticsCommand.ExecuteAsync(["--out", path], new(output, new StringWriter()), true,
            TestContext.Current.CancellationToken, fixture.Combine("missing-runtime"));
        Assert.Equal(73, code);
        Assert.DoesNotContain("PRIVATE", output.ToString());
        Assert.Contains("output_exists", output.ToString());
        Assert.Equal("keep", File.ReadAllText(path));
        Assert.False(Directory.Exists(fixture.Combine("missing-runtime")));
    }

    [Theory]
    [InlineData("--out")]
    [InlineData("--out", "one", "--out", "two")]
    [InlineData("--include-prompts")]
    [InlineData("--out", " ")]
    public async Task Invalid_options_are_refused(params string[] args)
    {
        using var fixture = new TemporaryDirectory("diagnostic-invalid-command");
        Assert.Equal(2, await DiagnosticsCommand.ExecuteAsync(args, new(new StringWriter(), new StringWriter()), true,
            TestContext.Current.CancellationToken, fixture.Path));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void Diagnostics_help_and_json_operand_are_part_of_the_shared_command_contract()
    {
        Assert.True(CliHelp.Resolve("localai", ["diagnostics", "--help"])?.Success);
        Assert.Equal(["diagnostics", "--out", "--json"], MachineOutput.Without(["diagnostics", "--out", "--json", "--json"]));
        Assert.False(MachineOutput.Requested(["diagnostics", "--out", "--json"]));
        Assert.True(MachineOutput.Supports("diagnostics"));
    }

    [Fact]
    public async Task Real_cli_previews_projected_contents_and_reports_no_model_call()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LOCALAI_LANGUAGE"] = "en";
        start.ArgumentList.Add(Assembly.Load("localai").Location);
        foreach (var argument in new[] { "diagnostics", "--json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.Equal(0, process.ExitCode);
        using var json = JsonDocument.Parse(await output);
        var data = json.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("saved").GetBoolean());
        Assert.Equal(5, data.GetProperty("contents").EnumerateObject().Count());
        Assert.Contains("prompts", data.GetProperty("manifest").GetProperty("excluded").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("No model call", await error);
        Assert.Contains("Token savings do not apply to this operation.", await error);
    }
}
