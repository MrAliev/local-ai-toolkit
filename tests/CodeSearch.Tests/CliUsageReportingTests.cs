using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CodeSearch.Tests;

public sealed class CliUsageReportingTests
{
    [Theory]
    [InlineData("en", "--help", 0, "Usage:")]
    [InlineData("ru", "--help", 0, "Использование:")]
    [InlineData("en", "search --help", 0, "--query")]
    [InlineData("ru", "help index", 0, "Использование:")]
    [InlineData("en", "--help --bogus", 1, "")]
    [InlineData("en", "search --help --bogus", 0, "--query")]
    [InlineData("en", "help missing", 1, "")]
    public async Task Help_has_consistent_streams_language_and_argument_validation(string language, string arguments, int exit, string expected)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.Environment["LOCALAI_LANGUAGE"] = language;
        start.ArgumentList.Add(Assembly.Load("codesearch").Location);
        foreach (var argument in arguments.Split(' ')) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(exit, process.ExitCode);
        Assert.Contains(expected, await output);
        if (exit != 0) Assert.Contains("Unknown help arguments", await error);
    }

    // Where Git finds no repository, so the status this printed before the fix reads nothing real.
    [Fact]
    public async Task A_help_flag_after_an_option_prints_help_instead_of_running()
    {
        var outside = Directory.CreateTempSubdirectory("codesearch-help-outside-git-");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = outside.FullName,
            };
            start.Environment["LOCALAI_LANGUAGE"] = "en";
            start.Environment["GIT_DIR"] = Path.Combine(outside.FullName, "no-repository");
            start.ArgumentList.Add(Assembly.Load("codesearch").Location);
            foreach (var argument in new[] { "status", "--root", ".", "--help" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await error;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("codesearch status [--root <dir>] [--json]", await output);
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("capabilities", 0)]
    [InlineData("unknown-command", 1)]
    public async Task Real_cli_preserves_json_stdout_and_reports_success_and_refusal(string command, int exit)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Assembly.Load("codesearch").Location);
        start.ArgumentList.Add(command); start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        using var json = JsonDocument.Parse(await output);
        Assert.Equal(exit, process.ExitCode);
        Assert.Matches(@"Call [0-9a-f-]{36}", await error);
    }
}
