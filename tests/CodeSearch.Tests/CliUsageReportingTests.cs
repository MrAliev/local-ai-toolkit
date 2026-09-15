using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CodeSearch.Tests;

public sealed class CliUsageReportingTests
{
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
