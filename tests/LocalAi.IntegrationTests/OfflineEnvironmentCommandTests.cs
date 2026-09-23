using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LocalAi.IntegrationTests;

public sealed class OfflineEnvironmentCommandTests
{
    [Theory]
    [InlineData("bundle", "offline_source_invalid")]
    [InlineData("verify", "offline_verification_failed")]
    public async Task Real_cli_reports_missing_materials_without_running_a_model(string operation, string refusal)
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-offline-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LOCALAI_LANGUAGE"] = "en";
        start.ArgumentList.Add(Assembly.Load("localai").Location);
        string[] arguments = operation == "bundle" ? ["offline", operation, "--source", missing, "--out", missing + "-output", "--json"]
            : ["offline", operation, "--bundle", missing, "--manifest-sha256", new string('0', 64), "--json"];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try { await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.Equal(65, process.ExitCode);
        using var json = JsonDocument.Parse(await output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(refusal, json.RootElement.GetRawText());
        Assert.DoesNotContain(missing, json.RootElement.GetRawText());
        Assert.Contains("No model call", await error);
    }
}
