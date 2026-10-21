using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.TestSupport;

namespace LocalAi.Installer.Core.Tests;

internal static class ScipVersionProbe
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    internal const int OutputLimitBytes = 64 * 1024;
    internal const string DiagnosticsVariable = "LOCALAI_TEST_DIAGNOSTICS";
    private static readonly TimeSpan MetadataBudget = TimeSpan.FromSeconds(2);

    internal static async Task VerifyAsync(
        string command,
        string executable,
        string expectedVersion,
        IProcessRunner runner,
        IReadOnlyCollection<string> excused,
        Func<string, string?> resolveExecutable,
        CancellationToken cancellationToken,
        string? diagnosticsRoot = null)
    {
        ProcessResult? result = null;
        Exception? runnerError = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            result = await runner.RunAsync(executable, ["--version"], Budget, cancellationToken);
        }
        catch (Exception exception)
        {
            runnerError = exception;
        }
        elapsed.Stop();

        if (result is { ExitCode: 0, TimedOut: false, Cancelled: false } &&
            (result.StandardOutput + result.StandardError).Contains(expectedVersion, StringComparison.Ordinal))
        {
            return;
        }

        // Only a completed nonzero exit is excused. A nullable exit is not evidence of
        // that outcome, and a timeout/cancellation must remain visible even on the allowlist.
        var isExcused = result is { ExitCode: not null and not 0, TimedOut: false, Cancelled: false } &&
                        FixturePrerequisite.IsExcused(command, excused);
        var runtime = new List<RuntimeVersion>();
        foreach (var tool in new[] { "node", "npm" })
        {
            runtime.Add(await ReadRuntimeVersionAsync(tool, runner, resolveExecutable));
        }

        string evidence;
        try
        {
            evidence = WriteEvidence(
                diagnosticsRoot ?? Environment.GetEnvironmentVariable(DiagnosticsVariable),
                command, executable, expectedVersion, result, runnerError, elapsed.Elapsed,
                cancellationToken.IsCancellationRequested, excused, isExcused, runtime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            evidence = $"unavailable ({exception.GetType().Name}: {Bound(exception.Message, 512)})";
        }

        var summary = $"{command} ({executable}) --version failed: " +
                      $"ExitCode={result?.ExitCode?.ToString() ?? "null"}; " +
                      $"TimedOut={result?.TimedOut ?? false}; " +
                      $"Cancelled={result?.Cancelled ?? runnerError is OperationCanceledException}; " +
                      $"ElapsedMs={elapsed.Elapsed.TotalMilliseconds:F0}; BudgetMs={Budget.TotalMilliseconds:F0}; " +
                      $"RunnerError={runnerError?.GetType().Name ?? "none"}; Evidence={evidence}";
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        if (isExcused)
        {
            return;
        }

        Assert.Fail(summary);
    }

    private static string WriteEvidence(
        string? root, string command, string executable, string expectedVersion,
        ProcessResult? result, Exception? error, TimeSpan elapsed, bool cancellationRequested,
        IReadOnlyCollection<string> excused, bool isExcused, IReadOnlyList<RuntimeVersion> runtime)
    {
        var directory = string.IsNullOrWhiteSpace(root)
            ? Directory.CreateTempSubdirectory("localai-scip-probe-").FullName
            : Directory.CreateDirectory(Path.Combine(Path.GetFullPath(root), "scip-probe-" + Guid.NewGuid().ToString("N"))).FullName;
        var stdoutTruncated = WriteBounded(Path.Combine(directory, "stdout.log"), result?.StandardOutput ?? "");
        var stderrTruncated = WriteBounded(Path.Combine(directory, "stderr.log"), result?.StandardError ?? "");
        var report = new
        {
            schemaVersion = 1,
            command,
            executable,
            arguments = new[] { "--version" },
            expectedVersion,
            elapsedMs = elapsed.TotalMilliseconds,
            budgetMs = Budget.TotalMilliseconds,
            exitCode = result?.ExitCode,
            timedOut = result?.TimedOut ?? false,
            cancelled = result?.Cancelled ?? error is OperationCanceledException,
            cancellationRequested,
            runnerReturned = result is not null,
            runnerError = error is null ? null : new { type = error.GetType().FullName, message = Bound(error.Message, 4096) },
            standardOutputTruncated = result?.StandardOutputTruncated ?? false,
            standardErrorTruncated = result?.StandardErrorTruncated ?? false,
            stdoutArtifactTruncated = stdoutTruncated,
            stderrArtifactTruncated = stderrTruncated,
            outputLimitBytes = OutputLimitBytes,
            excusedFixtures = excused.Take(64).Select(value => Bound(value, 128)).ToArray(),
            excusedFixturesTruncated = excused.Count > 64 || excused.Any(value => value.Length > 128),
            isExcused,
            runtime,
        };
        File.WriteAllText(Path.Combine(directory, "probe.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
        return directory;
    }

    private static bool WriteBounded(string path, string text)
    {
        var bytes = new byte[OutputLimitBytes];
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(), flush: true,
            out _, out var bytesUsed, out var completed);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes.AsSpan(0, bytesUsed));
        return !completed;
    }

    private static async Task<RuntimeVersion> ReadRuntimeVersionAsync(
        string command, IProcessRunner runner, Func<string, string?> resolveExecutable)
    {
        string? executable = null;
        try
        {
            executable = resolveExecutable(command);
            if (executable is null)
            {
                return new(command, null, null, null, false, false, false, "not_found");
            }

            // This evidence has its own small budget. It never retries or extends the SCIP probe.
            var result = await runner.RunAsync(executable, ["--version"], MetadataBudget, CancellationToken.None);
            var candidate = result.StandardOutput.Trim();
            var version = candidate.Length <= 128 && Regex.IsMatch(candidate, @"^v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$")
                ? candidate
                : null;
            return new(command, executable, version, result.ExitCode, result.TimedOut, result.Cancelled,
                result.StandardOutputTruncated || result.StandardErrorTruncated, null);
        }
        catch (Exception exception)
        {
            return new(command, executable, null, null, false,
                exception is OperationCanceledException, false, exception.GetType().FullName);
        }
    }

    private static string Bound(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record RuntimeVersion(
        string Command, string? Executable, string? Version, int? ExitCode,
        bool TimedOut, bool Cancelled, bool OutputTruncated, string? Error);
}
