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
    public async Task A_development_consumer_can_cold_start_its_copied_broker_and_complete_a_status_request()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LocalAi.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);

        var consumer = Path.Combine(Path.GetTempPath(), "localai-development-consumer-" + Guid.NewGuid().ToString("N"));
        var unrelatedDirectoryName = "unrelated-output-" + Guid.NewGuid().ToString("N");
        var unrelatedOutput = Path.Combine(repository.FullName, "src", "LocalAi.Broker", "bin", "Release", "net10.0", unrelatedDirectoryName);
        Directory.CreateDirectory(unrelatedOutput);
        File.WriteAllText(Path.Combine(unrelatedOutput, "unrelated.txt"), "Not an output of this build.");
        Directory.CreateDirectory(consumer);
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
            Assert.Equal(0, await RunToExitAsync(start, Path.Combine(consumer, "test-build.log")));
            Assert.False(File.Exists(Path.Combine(consumer, "bin", "Release", "net10.0", unrelatedDirectoryName, "unrelated.txt")));

            var executable = Path.Combine(consumer, "bin", "Release", "net10.0", "LocalAi.Broker.exe");
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
            Assert.Equal(2, await RunToExitAsync(help, Path.Combine(consumer, "test-entry-point.log")));

            await RunScenarioAsync(Path.Combine(consumer, "runtime"), ["fixture/custom:latest"], executable);
            succeeded = true;
        }
        finally
        {
            File.Delete(Path.Combine(unrelatedOutput, "unrelated.txt"));
            Directory.Delete(unrelatedOutput);
            if (succeeded) Directory.Delete(consumer, recursive: true);
            else TestContext.Current.TestOutputHelper?.WriteLine("Development build diagnostics retained at " + consumer);
        }
    }

    private static async Task<int> RunToExitAsync(ProcessStartInfo start, string logPath)
    {
        using var process = Process.Start(start)!;
        // Teardown must retain both streams even if the test is cancelled.
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            File.WriteAllText(logPath, await error + await output);
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
            Assert.True(File.Exists(Path.Combine(root, "test-failure.txt")));
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

    private static async Task RunScenarioAsync(string root, string[] expectedMissing, string? executable = null)
    {
        // Only a hang guard: startup and one local status request have no timing assertion.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromMinutes(2));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backend = ServeEmptyModelListsAsync(listener, guard.Token);
        Process? process = null;
        Task<string>? error = null;
        Task<string>? output = null;
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
                    // Teardown stops and waits for the child before draining its output.
                    // A cancelled request must not discard the diagnostics needed to explain it.
                    error = process.StandardError.ReadToEndAsync();
                    output = process.StandardOutput.ReadToEndAsync();
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
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "test-failure.txt"), exception.ToString());
            TestContext.Current.TestOutputHelper?.WriteLine("Broker diagnostics retained at " + root);
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
                    File.WriteAllText(Path.Combine(root, "test-broker.log"), await error + await output);
                    File.WriteAllText(Path.Combine(root, "test-process.json"), JsonSerializer.Serialize(new
                    {
                        process.Id,
                        process.ExitCode,
                        RuntimeRoot = root,
                    }));
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
