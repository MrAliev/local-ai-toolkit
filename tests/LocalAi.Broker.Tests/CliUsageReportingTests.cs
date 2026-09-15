using System.Text;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class CliUsageReportingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-cli-usage-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("localai", "capabilities", 0)]
    [InlineData("localai", "translate", 2)]
    [InlineData("codesearch", "search", 1)]
    public async Task Json_stdout_is_unchanged_and_one_report_counts_both_streams(string component, string command, int exit)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var id = Guid.NewGuid();
        const string json = "{\"ok\":false,\"text\":\"я😀\"}";
        var code = await CliUsageReporting.RunAsync(component, [command, "--json"], root, output, error,
            async (stdout, stderr) =>
            {
                await stdout.WriteLineAsync(json);
                await stderr.WriteLineAsync("diagnostic");
                return exit;
            }, id);
        Assert.Equal(exit, code);
        Assert.Equal(json + Environment.NewLine, output.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var report = await Report(id);
        Assert.Contains(id.ToString(), error.ToString(), StringComparison.Ordinal);
        Assert.Equal(exit == 0 ? UsageTechnicalOutcome.Succeeded : UsageTechnicalOutcome.Refused, report.Outcome.Technical);
        Assert.Equal(Encoding.UTF8.GetByteCount(output + error.ToString()), report.Volumes[UsageVolume.ResponseBytes].Value);
        Assert.Equal(output.ToString().Length + error.ToString().Length, report.Volumes[UsageVolume.ResponseCharacters].Value);
        Assert.Single((await new UsageReportStore(root).ReadForReportAsync(TestContext.Current.CancellationToken)).Reports);
    }

    [Fact]
    public async Task An_unwritable_report_store_preserves_the_command_result()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "telemetry"), "blocked", TestContext.Current.CancellationToken);
        using var output = new StringWriter(); using var error = new StringWriter();
        var id = Guid.NewGuid();
        var exit = await CliUsageReporting.RunAsync("localai", ["capabilities"], root, output, error,
            (stdout, _) => { stdout.Write("answer"); return Task.FromResult(0); }, id);
        Assert.Equal(0, exit); Assert.Equal("answer", output.ToString());
        Assert.Contains("could not be saved", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(id.ToString(), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installer_progress_protocol_has_no_extra_stderr_records_but_keeps_a_report()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var id = Guid.NewGuid();
        const string progress = "{\"schemaVersion\":1,\"operation\":\"pull\",\"phase\":\"download\"}";
        await CliUsageReporting.RunAsync("localai", ["model", "pull", "sample", "--progress-json"], root,
            output, error, (_, stderr) => { stderr.WriteLine(progress); return Task.FromResult(0); }, id);
        Assert.Equal(progress + Environment.NewLine, error.ToString());
        var report = await Report(id);
        Assert.Equal(Encoding.UTF8.GetByteCount(error.ToString()), report.Volumes[UsageVolume.ResponseBytes].Value);
    }

    [Fact]
    public async Task Writes_split_between_surrogate_halves_are_counted_as_the_delivered_utf8()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var id = Guid.NewGuid();
        await CliUsageReporting.RunAsync("codesearch", ["status"], root, output, error, (stdout, _) =>
        {
            stdout.Write('\ud83d'); stdout.Write('\ude00');
            return Task.FromResult(0);
        }, id);
        var report = await Report(id);
        Assert.Equal(Encoding.UTF8.GetByteCount(output.ToString()) + Encoding.UTF8.GetByteCount(error.ToString()), report.Volumes[UsageVolume.ResponseBytes].Value);
    }

    [Fact]
    public async Task An_output_failure_does_not_become_successful_delivery()
    {
        using var error = new StringWriter();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => CliUsageReporting.RunAsync("localai", ["ask"], root,
            new FailedWriter(), error, (stdout, _) => { stdout.Write("answer"); return Task.FromResult(0); }, id));
        Assert.Equal(UsageDelivery.Failed, (await Report(id)).Outcome.Delivery);
        Assert.Equal(CloudTokenMethodology.Version, (await Report(id)).MethodologyVersion);
    }

    private async Task<UsageReport> Report(Guid id)
    {
        var report = await new UsageReportStore(root).ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(report); return report;
    }
    private sealed class FailedWriter : StringWriter { public override void Write(string? value) => throw new IOException("closed output"); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
