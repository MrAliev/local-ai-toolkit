using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LocalAi.IntegrationTests;

public sealed class HardwarePlanCommandTests
{
    [Fact]
    public async Task Real_cli_refuses_an_unsigned_release_before_hardware_or_model_work()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LOCALAI_LANGUAGE"] = "en";
        start.ArgumentList.Add(Assembly.Load("localai").Location);
        foreach (var argument in new[] { "hardware", "plan", "--release", Path.Combine(Path.GetTempPath(), "missing-release-" + Guid.NewGuid().ToString("N")), "--json" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.Equal(65, process.ExitCode);
        using var json = JsonDocument.Parse(await output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("release_manifest_rejected", json.RootElement.GetRawText());
        Assert.Contains("No model call", await error);
    }
}
