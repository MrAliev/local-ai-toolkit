using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ConfiguredCatalogStartupTests
{
    [Fact]
    public Task The_broker_process_uses_the_catalog_from_its_runtime_directory() =>
        RunScenarioAsync(NewRoot(), ["fixture/custom:latest"]);

    [Fact]
    public Task A_development_consumer_can_cold_start_its_copied_broker_and_complete_a_status_request() =>
        RunDevelopmentConsumerAsync(NewConsumerRoot());

    [Fact]
    public async Task A_consumer_cleanup_failure_preserves_the_completed_scenario_evidence()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The read-only executable deletion fixture is Windows-specific.");
        var consumer = NewConsumerRoot();
        var diagnostics = DiagnosticsDirectoryFor(consumer);
        string? executable = null;
        try
        {
            var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                RunDevelopmentConsumerAsync(consumer, path =>
                {
                    executable = path;
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }));

            // Read-only is a deterministic cleanup failure, not a claim about the CI failure's cause.
            Assert.True(Directory.Exists(diagnostics), "Cleanup must retain evidence outside the deleted consumer/runtime tree.");
            Assert.True(File.Exists(Path.Combine(diagnostics, "test-build.log")));
            Assert.True(File.Exists(Path.Combine(diagnostics, "test-entry-point.log")));
            Assert.True(File.Exists(Path.Combine(diagnostics, "test-broker.log")));
            Assert.True(File.Exists(Path.Combine(diagnostics, "test-build.json")));
            Assert.True(File.Exists(Path.Combine(diagnostics, "test-entry-point.json")));
            using var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnostics, "test-process.json")));
            Assert.True(process.RootElement.GetProperty("Id").GetInt32() > 0);
            Assert.True(process.RootElement.GetProperty("StartedAtUtc").GetDateTimeOffset() > DateTimeOffset.MinValue);
            Assert.Equal(JsonValueKind.Number, process.RootElement.GetProperty("ExitCode").ValueKind);
            using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnostics, "test-cleanup-failure.json")));
            Assert.Equal(consumer, cleanup.RootElement.GetProperty("ConsumerRoot").GetString());
            Assert.True(cleanup.RootElement.GetProperty("ScenarioCompleted").GetBoolean());
            Assert.Equal(typeof(UnauthorizedAccessException).FullName, cleanup.RootElement.GetProperty("ExceptionType").GetString());
            Assert.Equal(failure.HResult, cleanup.RootElement.GetProperty("HResult").GetInt32());
            Assert.Equal(process.RootElement.GetProperty("Id").GetInt32(),
                cleanup.RootElement.GetProperty("OwnedProcess").GetProperty("Id").GetInt32());
            Assert.True(cleanup.RootElement.GetProperty("CopiedExecutable").GetProperty("ReadOnly").GetBoolean());
            Assert.True(cleanup.RootElement.GetProperty("SourceExecutable").GetProperty("Exists").GetBoolean());
        }
        finally
        {
            if (executable is not null && File.Exists(executable))
                File.SetAttributes(executable, File.GetAttributes(executable) & ~FileAttributes.ReadOnly);
            if (Directory.Exists(consumer)) Directory.Delete(consumer, recursive: true);
            if (Directory.Exists(diagnostics)) Directory.Delete(diagnostics, recursive: true);
        }
    }

    private static string NewConsumerRoot() =>
        Path.Combine(Path.GetTempPath(), "localai-development-consumer-" + Guid.NewGuid().ToString("N"));

    private static string DiagnosticsDirectoryFor(string consumer) =>
        Environment.GetEnvironmentVariable("LOCALAI_TEST_DIAGNOSTICS") is { Length: > 0 } configured
            ? Path.Combine(configured, Path.GetFileName(consumer)) : consumer + "-diagnostics";

    [Fact]
    public async Task Process_evidence_retains_bounded_output_and_marks_truncation()
    {
        using var reader = new StringReader(new string('Ж', 40_000) + "last failure detail");
        var captured = await ReadDiagnosticOutputAsync(reader);
        Assert.True(captured.Truncated);
        Assert.EndsWith("last failure detail", captured.Text, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(captured.Text + "\n" + captured.Text) <= 65_536);
    }

    [Fact]
    public async Task A_truncated_diagnostic_tail_remains_valid_for_strict_utf8_encoding()
    {
        using var reader = new StringReader(string.Concat(Enumerable.Repeat("😀", 5_001)) + "z");
        var captured = await ReadDiagnosticOutputAsync(reader);
        Assert.True(captured.Truncated);
        var bytes = new UTF8Encoding(false, true).GetBytes(captured.Text);
        Assert.Equal(captured.Text, Encoding.UTF8.GetString(bytes));
        Assert.EndsWith("😀z", captured.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unwritable_evidence_directory_does_not_replace_the_cleanup_failure()
    {
        var consumer = NewConsumerRoot();
        Directory.CreateDirectory(consumer);
        var blockedDiagnostics = Path.Combine(consumer, "evidence-is-a-file");
        File.WriteAllText(blockedDiagnostics, "fixture");
        var original = new UnauthorizedAccessException("original cleanup failure");
        try
        {
            PreserveCleanupFailure(consumer, blockedDiagnostics, "missing-source.exe", "missing-copy.exe", original);
            Assert.Equal("original cleanup failure", original.Message);
            Assert.Equal(typeof(IOException).FullName, original.Data["EvidenceWriteFailure"]);
            Assert.IsType<int>(original.Data["EvidenceWriteHResult"]);
        }
        finally { Directory.Delete(consumer, recursive: true); }
    }

    private sealed record CapturedDiagnosticText(string Text, bool Truncated);

    private const int MaximumDiagnosticCharactersPerStream = 8_192;

    private static async Task<CapturedDiagnosticText> ReadDiagnosticOutputAsync(TextReader reader)
    {
        // Continue draining both pipes, but retain only their tails. Two such tails remain
        // below 64 KiB even with UTF-8's worst-case expansion of UTF-16 code units.
        var tail = new StringBuilder();
        var buffer = new char[2_048];
        var truncated = false;
        while (await reader.ReadAsync(buffer, CancellationToken.None) is var count && count > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length <= MaximumDiagnosticCharactersPerStream) continue;
            truncated = true;
            tail.Remove(0, tail.Length - MaximumDiagnosticCharactersPerStream);
            if (char.IsLowSurrogate(tail[0])) tail.Remove(0, 1);
        }
        return new(tail.ToString(), truncated);
    }

    private static void WriteDiagnosticLog(string path, CapturedDiagnosticText output, CapturedDiagnosticText error) =>
        File.WriteAllText(path, output.Text + "\n" + error.Text);

    private static void WriteEvidence(Action write, Exception? originalFailure = null)
    {
        try { write(); }
        catch (Exception evidenceFailure) when (originalFailure is not null)
        {
            originalFailure.Data["EvidenceWriteFailure"] = evidenceFailure.GetType().FullName;
            originalFailure.Data["EvidenceWriteHResult"] = evidenceFailure.HResult;
        }
    }

    private static async Task RunDevelopmentConsumerAsync(string consumer, Action<string>? beforeCleanup = null)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LocalAi.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);

        var unrelatedDirectoryName = "unrelated-output-" + Guid.NewGuid().ToString("N");
        var unrelatedOutput = Path.Combine(repository.FullName, "src", "LocalAi.Broker", "bin", "Release", "net10.0", unrelatedDirectoryName);
        var sourceExecutable = Path.Combine(repository.FullName, "src", "LocalAi.Broker", "bin", "Release", "net10.0", "LocalAi.Broker.exe");
        var executable = Path.Combine(consumer, "bin", "Release", "net10.0", "LocalAi.Broker.exe");
        var diagnostics = DiagnosticsDirectoryFor(consumer);
        Directory.CreateDirectory(unrelatedOutput);
        File.WriteAllText(Path.Combine(unrelatedOutput, "unrelated.txt"), "Not an output of this build.");
        Directory.CreateDirectory(consumer);
        Directory.CreateDirectory(diagnostics);
        var succeeded = false;
        try
        {
            // This consumer deliberately has no assembly reference to the broker or its libraries.
            // Only the real development-copy target can supply what its apphost needs to run.
            var project = Path.Combine(consumer, "Consumer.csproj");
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")),
                new XElement("Import", new XAttribute("Project", Path.Combine(repository.FullName, "src", "BrokerBinary.props")))))
                .Save(project);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = consumer,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[] { "build", project, "--configuration", "Release", "--nologo" })
                start.ArgumentList.Add(argument);
            Assert.Equal(0, await RunToExitAsync(start, Path.Combine(diagnostics, "test-build.log")));
            Assert.False(File.Exists(Path.Combine(consumer, "bin", "Release", "net10.0", unrelatedDirectoryName, "unrelated.txt")));

            // Fail immediately if the managed entry point cannot load, rather than waiting for
            // the readiness budget of a process whose apphost already exited.
            var help = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            help.ArgumentList.Add("--help");
            Assert.Equal(2, await RunToExitAsync(help, Path.Combine(diagnostics, "test-entry-point.log")));

            await RunScenarioAsync(Path.Combine(consumer, "runtime"), ["fixture/custom:latest"], executable, diagnostics);
            succeeded = true;
            beforeCleanup?.Invoke(executable);
        }
        finally
        {
            File.Delete(Path.Combine(unrelatedOutput, "unrelated.txt"));
            Directory.Delete(unrelatedOutput);
            if (succeeded)
            {
                try { Directory.Delete(consumer, recursive: true); }
                catch (Exception exception)
                {
                    PreserveCleanupFailure(consumer, diagnostics, sourceExecutable, executable, exception);
                    throw;
                }
                Directory.Delete(diagnostics, recursive: true);
            }
            else TestContext.Current.TestOutputHelper?.WriteLine("Development build diagnostics retained at " + diagnostics);
        }
    }

    private static void PreserveCleanupFailure(string consumer, string diagnostics, string sourceExecutable,
        string copiedExecutable, Exception exception)
    {
        WriteEvidence(() =>
        {
            Directory.CreateDirectory(diagnostics);
            var processPath = Path.Combine(diagnostics, "test-process.json");
            File.WriteAllText(Path.Combine(diagnostics, "test-cleanup-failure.json"), JsonSerializer.Serialize(new
            {
                ConsumerRoot = consumer,
                ScenarioCompleted = true,
                ExceptionType = exception.GetType().FullName,
                exception.HResult,
                Message = exception.Message.Length > 4_096 ? exception.Message[..4_096] : exception.Message,
                MessageTruncated = exception.Message.Length > 4_096,
                SourceExecutable = DescribeExecutable(sourceExecutable),
                CopiedExecutable = DescribeExecutable(copiedExecutable),
                OwnedProcess = File.Exists(processPath)
                    ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(processPath)) : (JsonElement?)null,
            }));
        }, exception);
        WriteEvidence(() =>
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Development consumer cleanup failed; consumer: " + consumer
                + "; diagnostics: " + diagnostics + "; HResult: " + exception.HResult);
        }, exception);
    }

    private static object DescribeExecutable(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return new { Path = path, Exists = true, Attributes = attributes.ToString(), AttributeBits = (int)attributes,
                ReadOnly = (attributes & FileAttributes.ReadOnly) != 0 };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new { Path = path, Exists = File.Exists(path), ErrorType = exception.GetType().FullName, exception.HResult };
        }
    }

    private static async Task<int> RunToExitAsync(ProcessStartInfo start, string logPath)
    {
        using var process = Process.Start(start)!;
        var startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        // Teardown must retain both streams even if the test is cancelled.
        var error = ReadDiagnosticOutputAsync(process.StandardError);
        var output = ReadDiagnosticOutputAsync(process.StandardOutput);
        Exception? originalFailure = null;
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var standardOutput = await output;
            var standardError = await error;
            WriteEvidence(() =>
            {
                WriteDiagnosticLog(logPath, standardOutput, standardError);
                File.WriteAllText(Path.ChangeExtension(logPath, ".json"), JsonSerializer.Serialize(new
                {
                    process.Id,
                    StartedAtUtc = startedAtUtc,
                    process.ExitCode,
                    Executable = start.FileName,
                    StandardOutputTruncated = standardOutput.Truncated,
                    StandardErrorTruncated = standardError.Truncated,
                    MaximumCharactersPerStream = MaximumDiagnosticCharactersPerStream,
                }));
            }, originalFailure);
        }
        return process.ExitCode;
    }

    [Fact]
    public async Task A_failed_scenario_keeps_process_output_and_queue_evidence()
    {
        var root = NewRoot();
        try
        {
            // A real status request completes, then the intentionally wrong expectation fails.
            // Its process output and job files must survive the same teardown as the main test.
            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
                RunScenarioAsync(root, ["intentionally-wrong-expectation"]));
            Assert.True(File.Exists(Path.Combine(root, "test-broker.log")));
            Assert.True(File.Exists(Path.Combine(root, "test-failure.json")));
            using var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test-process.json")));
            Assert.True(process.RootElement.GetProperty("Id").GetInt32() > 0);
            Assert.Equal(root, process.RootElement.GetProperty("RuntimeRoot").GetString());
            Assert.NotEmpty(Directory.EnumerateFiles(root, "request.json", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "localai-catalog-startup-" + Guid.NewGuid().ToString("N"));

    private static async Task RunScenarioAsync(string root, string[] expectedMissing, string? executable = null,
        string? diagnosticsDirectory = null)
    {
        diagnosticsDirectory ??= root;
        // Only a hang guard: startup and one local status request have no timing assertion.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromMinutes(2));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backend = ServeEmptyModelListsAsync(listener, guard.Token);
        Process? process = null;
        Task<CapturedDiagnosticText>? error = null;
        Task<CapturedDiagnosticText>? output = null;
        DateTimeOffset? startedAtUtc = null;
        Exception? originalFailure = null;
        var succeeded = false;
        try
        {
            var store = new ModelRoutingCatalogStore(root);
            var document = ModelRoutingCatalogResource.LoadDocument();
            var model = document.Models.First(entry => entry.InstallPolicy == LocalModelInstallPolicy.Recommended) with
            {
                Tag = "fixture/custom:latest", Source = "fixture/custom:latest",
            };
            document = document with
            {
                Models = [.. document.Models, model],
                MaintenanceAllowlist = [model.Tag],
            };
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(document, LocalAiJson.Strict));
            executable ??= Path.Combine(AppContext.BaseDirectory, "LocalAi.Broker.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[] { "serve", "--runtime", root, "--ollama", $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/" })
                start.ArgumentList.Add(argument);
            // Use the same readiness check as production. Only process creation is supplied
            // here so the test owns its child and captures both redirected streams.
            var broker = new BrokerProcess(executable, root,
                BrokerProcess.ReadState,
                state => process is not null && !process.HasExited && process.Id == state.ProcessId &&
                    new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) == state.StartedAtUtc,
                (_, _) =>
                {
                    process = Process.Start(start)!;
                    startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    // Teardown stops and waits for the child before draining its output.
                    // A cancelled request must not discard the diagnostics needed to explain it.
                    error = ReadDiagnosticOutputAsync(process.StandardError);
                    output = ReadDiagnosticOutputAsync(process.StandardOutput);
                    return process.Id;
                },
                TimeProvider.System, Task.Delay, startupTimeout: TimeSpan.FromMinutes(2));
            var client = new BrokerClient(new DurableQueue(root), broker);

            var result = await client.ExecuteAsync<LocalModelsStatusOutput>(
                LocalJobRequestFactory.CreateModelControl("catalog-startup", LocalJobPriority.Interactive, ModelControlOperation.Status),
                guard.Token);

            Assert.Equal(store.Read().CatalogVersion, result.Value.CatalogVersion);
            Assert.Equal(expectedMissing, result.Value.RecommendedMissingModels);
            succeeded = true;
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            WriteEvidence(() =>
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                var detail = exception.ToString();
                File.WriteAllText(Path.Combine(diagnosticsDirectory, "test-failure.json"), JsonSerializer.Serialize(new
                {
                    ExceptionType = exception.GetType().FullName,
                    exception.HResult,
                    Detail = detail.Length > 8_192 ? detail[..8_192] : detail,
                    DetailTruncated = detail.Length > 8_192,
                }));
                TestContext.Current.TestOutputHelper?.WriteLine("Broker diagnostics retained at " + diagnosticsDirectory);
            }, exception);
            throw;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                if (error is not null && output is not null)
                {
                    // Keep machine output in the test's private artifact, never in assertions.
                    var standardOutput = await output;
                    var standardError = await error;
                    WriteEvidence(() =>
                    {
                        Directory.CreateDirectory(diagnosticsDirectory);
                        WriteDiagnosticLog(Path.Combine(diagnosticsDirectory, "test-broker.log"), standardOutput, standardError);
                        File.WriteAllText(Path.Combine(diagnosticsDirectory, "test-process.json"), JsonSerializer.Serialize(new
                        {
                            process.Id,
                            StartedAtUtc = startedAtUtc,
                            process.ExitCode,
                            Executable = executable,
                            RuntimeRoot = root,
                            StandardOutputTruncated = standardOutput.Truncated,
                            StandardErrorTruncated = standardError.Truncated,
                            MaximumCharactersPerStream = MaximumDiagnosticCharactersPerStream,
                        }));
                    }, originalFailure);
                }
                process.Dispose();
            }
            guard.Cancel();
            listener.Stop();
            try { await backend; }
            catch (OperationCanceledException) { }
            if (succeeded && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ServeEmptyModelListsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
            const string body = "{\"models\":[]}";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, cancellationToken);
        }
    }
}
