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
    public async Task The_broker_process_uses_the_catalog_from_its_runtime_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "localai-catalog-startup-" + Guid.NewGuid().ToString("N"));
        // Only a hang guard: startup and one local status request have no timing assertion.
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromMinutes(2));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backend = ServeEmptyModelListsAsync(listener, guard.Token);
        Process? process = null;
        Task<string>? error = null;
        Task<string>? output = null;
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
            process = Process.Start(start)!;
            error = process.StandardError.ReadToEndAsync(guard.Token);
            output = process.StandardOutput.ReadToEndAsync(guard.Token);
            var client = new BrokerClient(new DurableQueue(root), new AlreadyStarted());

            var result = await client.ExecuteAsync<LocalModelsStatusOutput>(
                LocalJobRequestFactory.CreateModelControl("catalog-startup", LocalJobPriority.Interactive, ModelControlOperation.Status),
                guard.Token);

            Assert.Equal(store.Read().CatalogVersion, result.Value.CatalogVersion);
            Assert.Equal([model.Tag], result.Value.RecommendedMissingModels);
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
                    try { File.WriteAllText(Path.Combine(root, "test-broker.log"), await error + await output); }
                    catch (OperationCanceledException) { }
                }
                process.Dispose();
            }
            guard.Cancel();
            listener.Stop();
            try { await backend; }
            catch (OperationCanceledException) { }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    private sealed class AlreadyStarted : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
