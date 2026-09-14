using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ConfiguredCatalogStartupTests
{
    [Fact]
    public Task The_broker_process_uses_the_catalog_from_its_runtime_directory() =>
        RunScenarioAsync(NewRoot(), ["fixture/custom:latest"]);

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

    private static async Task RunScenarioAsync(string root, string[] expectedMissing)
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
            var executable = Path.Combine(AppContext.BaseDirectory, "LocalAi.Broker.exe");
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
