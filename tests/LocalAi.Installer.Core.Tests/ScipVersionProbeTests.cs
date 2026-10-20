using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.Installer.Core.Tests;

public sealed class ScipVersionProbeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localai-scip-evidence-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Timeout_exposes_command_outcome_and_bounded_evidence_without_retrying()
    {
        var runner = new FakeRunner(new(null, "partial output", "partial error", true, false));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ScipVersionProbe.VerifyAsync(
            "scip-typescript", @"C:\fixture\scip-typescript.cmd", "0.4.0", runner,
            ["scip-python"], Resolve, TestContext.Current.CancellationToken, root));

        Assert.Contains("scip-typescript", failure.Message, StringComparison.Ordinal);
        Assert.Contains("TimedOut=True", failure.Message, StringComparison.Ordinal);
        var evidence = Assert.Single(Directory.GetDirectories(root));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "probe.json")));
        Assert.Equal("scip-typescript", json.RootElement.GetProperty("command").GetString());
        Assert.True(json.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.Equal(5000, json.RootElement.GetProperty("budgetMs").GetDouble());
        Assert.Equal(@"C:\fixture\scip-typescript.cmd", json.RootElement.GetProperty("executable").GetString());
        Assert.True(json.RootElement.GetProperty("elapsedMs").GetDouble() >= 0);
        Assert.Equal("scip-python", json.RootElement.GetProperty("excusedFixtures")[0].GetString());
        Assert.Equal("v24.20.0", json.RootElement.GetProperty("runtime")[0].GetProperty("version").GetString());
        Assert.Equal("11.19.0", json.RootElement.GetProperty("runtime")[1].GetProperty("version").GetString());
        Assert.Equal("partial output", File.ReadAllText(Path.Combine(evidence, "stdout.log")));
        Assert.Equal(1, runner.ProbeCalls);
        Assert.Equal(TimeSpan.FromSeconds(5), runner.ProbeBudget);
    }

    [Fact]
    public async Task Multibyte_logs_are_valid_utf8_with_separate_capture_and_artifact_truncation_flags()
    {
        var runner = new FakeRunner(new(null, new string('Ж', 70000), string.Concat(Enumerable.Repeat("😀", 35000)),
            false, true, StandardOutputTruncated: true));
        await Assert.ThrowsAnyAsync<Exception>(() => VerifyAsync(runner));

        var evidence = Assert.Single(Directory.GetDirectories(root));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "probe.json")));
        Assert.True(json.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.True(json.RootElement.GetProperty("standardOutputTruncated").GetBoolean());
        Assert.False(json.RootElement.GetProperty("standardErrorTruncated").GetBoolean());
        Assert.True(json.RootElement.GetProperty("stdoutArtifactTruncated").GetBoolean());
        Assert.True(json.RootElement.GetProperty("stderrArtifactTruncated").GetBoolean());
        foreach (var name in new[] { "stdout.log", "stderr.log" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(evidence, name));
            Assert.InRange(bytes.Length, 1, ScipVersionProbe.OutputLimitBytes);
            Assert.NotEmpty(new UTF8Encoding(false, true).GetString(bytes));
        }
    }

    [Fact]
    public async Task Launch_error_and_failed_runtime_metadata_keep_the_primary_failure()
    {
        var runner = new FakeRunner(new(null, "", "", false, false))
        {
            ProbeError = new Win32Exception("controlled launch failure"),
            RuntimeError = new IOException("controlled metadata failure"),
        };
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => VerifyAsync(runner));
        Assert.Contains("RunnerError=Win32Exception", failure.Message, StringComparison.Ordinal);
        var evidence = Assert.Single(Directory.GetDirectories(root));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "probe.json")));
        Assert.False(json.RootElement.GetProperty("runnerReturned").GetBoolean());
        Assert.Equal("controlled launch failure", json.RootElement.GetProperty("runnerError").GetProperty("message").GetString());
        Assert.Equal(typeof(IOException).FullName, json.RootElement.GetProperty("runtime")[0].GetProperty("error").GetString());
        Assert.Equal(1, runner.ProbeCalls);
    }

    [Fact]
    public async Task Evidence_write_failure_does_not_replace_timeout_with_an_io_failure()
    {
        Directory.CreateDirectory(root);
        var blockedRoot = Path.Combine(root, "file-instead-of-directory");
        File.WriteAllText(blockedRoot, "owned test file");
        var runner = new FakeRunner(new(null, "", "", true, false));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ScipVersionProbe.VerifyAsync(
            "scip-typescript", Resolve("scip-typescript"), "0.4.0", runner, [], Resolve,
            TestContext.Current.CancellationToken, blockedRoot));
        Assert.Contains("TimedOut=True", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Evidence=unavailable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_probe_creates_no_evidence_and_does_not_inspect_runtime_metadata()
    {
        var runner = new FakeRunner(new(0, "scip-typescript 0.4.0", "", false, false));
        await VerifyAsync(runner);
        Assert.False(Directory.Exists(root));
        Assert.Equal(1, runner.ProbeCalls);
        Assert.Equal(0, runner.RuntimeCalls);
    }

    [Fact]
    public async Task Excused_nonzero_exit_still_records_evidence_without_changing_the_exemption()
    {
        var runner = new FakeRunner(new(17, "", "known fixture failure", false, false));
        await ScipVersionProbe.VerifyAsync("scip-typescript", Resolve("scip-typescript"), "0.4.0",
            runner, ["scip-typescript"], Resolve, TestContext.Current.CancellationToken, root);
        var evidence = Assert.Single(Directory.GetDirectories(root));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "probe.json")));
        Assert.True(json.RootElement.GetProperty("isExcused").GetBoolean());
        Assert.Equal(17, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    private Task VerifyAsync(IProcessRunner runner) => ScipVersionProbe.VerifyAsync(
        "scip-typescript", Resolve("scip-typescript"), "0.4.0", runner, [], Resolve,
        TestContext.Current.CancellationToken, root);

    private static string Resolve(string command) => @"C:\fixture\" + command + ".cmd";

    private sealed class FakeRunner(ProcessResult outcome) : IProcessRunner
    {
        public int ProbeCalls { get; private set; }
        public TimeSpan ProbeBudget { get; private set; }
        public int RuntimeCalls { get; private set; }
        public Exception? ProbeError { get; init; }
        public Exception? RuntimeError { get; init; }

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(["--version"], arguments);
            if (executable.EndsWith("scip-typescript.cmd", StringComparison.Ordinal))
            {
                ProbeCalls++;
                ProbeBudget = timeout;
                if (ProbeError is not null)
                {
                    throw ProbeError;
                }
                return Task.FromResult(outcome);
            }

            RuntimeCalls++;
            if (RuntimeError is not null)
            {
                throw RuntimeError;
            }
            return Task.FromResult(new ProcessResult(0, executable.EndsWith("node.cmd", StringComparison.Ordinal) ? "v24.20.0" : "11.19.0", "", false, false));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
