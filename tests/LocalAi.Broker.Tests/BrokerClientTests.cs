using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerClientTests
{
    [Fact]
    public async Task Embed_enqueues_starts_host_and_returns_typed_output()
    {
        using var usage = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        using var fixture = new BrokerClientFixture();
        var request = LocalJobRequestFactory.CreateEmbed(
            "embed:test",
            LocalJobPriority.Interactive,
            "embed-model",
            ["first"]);

        var pending = fixture.Client.ExecuteAsync<EmbedJobOutput>(
            request,
            TestContext.Current.CancellationToken);
        await fixture.Process.Started.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await fixture.CompleteNextAsync(
            JsonSerializer.SerializeToElement(
                new EmbedJobOutput([[1d, 2d]]),
                LocalAiJson.Strict));

        var result = await pending;

        Assert.Single(result.Value.Embeddings);
        Assert.Equal([1d, 2d], result.Value.Embeddings[0]);
        Assert.Equal(request.JobId, result.Receipt.JobId);
        Assert.Equal(1, fixture.Process.EnsureCalls);
        await UsageEvidence.CollectAsync(usage);
        var report = usage.Complete();
        Assert.Equal([request.JobId], report.Identity.JobIds);
        Assert.Equal("embed-model", Assert.Single(report.Models.Actual).Model);
    }

    [Fact]
    public async Task Failure_is_reported_without_exposing_request_content()
    {
        using var fixture = new BrokerClientFixture();
        var request = LocalJobRequestFactory.CreateChat(
            "chat:test",
            LocalJobPriority.Foreground,
            "chat-model",
            "SECRET-PROMPT",
            null,
            []);

        var pending = fixture.Client.ExecuteAsync<ChatJobOutput>(
            request,
            TestContext.Current.CancellationToken);
        await fixture.Process.Started.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await fixture.FailNextAsync("HttpRequestException");

        var error = await Assert.ThrowsAsync<BrokerJobFailedException>(() => pending);
        Assert.Contains("HttpRequestException", error.Message);
        Assert.DoesNotContain("SECRET-PROMPT", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_stops_waiting_without_cancelling_shared_job()
    {
        using var usage = new UsageCall(UsageSurface.Mcp, "locallm", "local_models_status");
        using var fixture = new BrokerClientFixture();
        using var cancellation = new CancellationTokenSource();
        var request = LocalJobRequestFactory.CreateListModels(
            "models:test",
            LocalJobPriority.Interactive);

        var pending = fixture.Client.ExecuteAsync<ListModelsJobOutput>(
            request,
            cancellation.Token);
        await fixture.Process.Started.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var diagnostic = await fixture.Queue.GetDiagnosticAsync(
            request.JobId,
            TestContext.Current.CancellationToken);
        Assert.Equal(LocalJobState.Queued, diagnostic!.State);
        Assert.Equal([request.JobId], usage.Complete().Identity.JobIds);
    }

    [Fact]
    public async Task Polling_allocations_do_not_scale_with_image_payload_size()
    {
        var small = await MeasurePollingAllocations(64);
        var large = await MeasurePollingAllocations(8 * 1024 * 1024);

        Assert.True(large < small + 512 * 1024,
            $"Ten polls allocated {small:N0} bytes for a small request and {large:N0} for an 8 MiB request.");
    }

    private static async Task<long> MeasurePollingAllocations(int imageCharacters)
    {
        using var fixture = new BrokerClientFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var polls = 0;
        long started = 0, allocated = 0;
        var client = new BrokerClient(fixture.Queue, fixture.Process, (_, _) =>
        {
            // Enqueue and the first poll have already completed. The remaining polls run
            // synchronously on this thread; measure only their allocations, not the payload setup.
            polls++;
            if (polls == 1)
            {
                started = GC.GetAllocatedBytesForCurrentThread();
            }
            else if (polls == 11)
            {
                allocated = GC.GetAllocatedBytesForCurrentThread() - started;
                stop.Cancel();
            }
            return Task.CompletedTask;
        });
        var request = LocalJobRequestFactory.CreateChat(
            "allocation-probe", LocalJobPriority.Foreground, "fixture", "prompt", null,
            [new string('A', imageCharacters)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync<ChatJobOutput>(request, stop.Token));

        Assert.Equal(11, polls);
        return allocated;
    }

    private sealed class BrokerClientFixture : IDisposable
    {
        /// <summary>
        /// The client's own deadline, set far beyond anything a correct run needs.
        ///
        /// It used to be five seconds, which is a measurement of how busy the machine is
        /// rather than of whether the client works: on a loaded CI runner the poll loop ran
        /// out of budget and the test died inside the queue's mutex with "The operation was
        /// canceled", pointing at nothing. Hang detection belongs to the run timeout CI
        /// already applies to every test project; this value only has to be large enough
        /// never to expire while the code under test is working.
        /// </summary>
        private static readonly TimeSpan ClientTimeout = TimeSpan.FromMinutes(5);

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "localai-client-" + Guid.NewGuid().ToString("N"));

        public BrokerClientFixture()
        {
            Queue = new DurableQueue(_root);
            Process = new FakeBrokerProcess();
            Client = new BrokerClient(
                Queue,
                Process,
                static (delay, token) => Task.Delay(TimeSpan.FromMilliseconds(5), token),
                ClientTimeout);
        }

        public DurableQueue Queue { get; }

        public FakeBrokerProcess Process { get; }

        public BrokerClient Client { get; }

        public async Task CompleteNextAsync(JsonElement body)
        {
            var lease = await Queue.LeaseNextAsync("test-worker")
                ?? throw new InvalidOperationException("Expected queued work.");
            var now = DateTimeOffset.UtcNow;
            var envelope = new BrokerResponseEnvelope(
                body,
                ReceiptFactory.Create(lease.Request, now, now));
            await Queue.CompleteAsync(
                lease.Request.JobId,
                lease.WorkerId,
                lease.LeaseId,
                JsonSerializer.SerializeToElement(envelope, LocalAiJson.Strict));
        }

        public async Task FailNextAsync(string failureCode)
        {
            var lease = await Queue.LeaseNextAsync("test-worker")
                ?? throw new InvalidOperationException("Expected queued work.");
            await Queue.FailAsync(
                lease.Request.JobId,
                lease.WorkerId,
                lease.LeaseId,
                failureCode,
                failureReason: null);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeBrokerProcess : IBrokerProcess
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EnsureCalls { get; private set; }

        public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            Started.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
