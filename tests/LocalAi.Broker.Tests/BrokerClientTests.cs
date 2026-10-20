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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_client_recovers_a_disappeared_worker_without_another_request(bool wasLeased)
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new RecoveryClock();
        var queue = new DurableQueue(root.Path, clock, TimeSpan.FromMinutes(1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = LocalJobRequestFactory.CreateListModels("recover-waiting-client", LocalJobPriority.Interactive);
        var process = new RecoveryProcess(async (call, cancellationToken) =>
        {
            if (call == 1) return;
            var lease = await queue.LeaseNextAsync("replacement-worker", cancellationToken);
            Assert.NotNull(lease);
            Assert.Equal(request.JobId, lease.Request.JobId);
            var response = new BrokerResponseEnvelope(
                JsonSerializer.SerializeToElement(new ListModelsJobOutput([]), LocalAiJson.Strict),
                ReceiptFactory.Create(lease.Request, clock.GetUtcNow(), clock.GetUtcNow()));
            await queue.CompleteAsync(lease.Request.JobId, lease.WorkerId, lease.LeaseId,
                JsonSerializer.SerializeToElement(response, LocalAiJson.Strict), cancellationToken);
        });
        var polls = 0;
        var client = new BrokerClient(queue, process, async (_, cancellationToken) =>
        {
            if (++polls == 1)
            {
                // A worker disappears either before leasing or after it acquired the job.
                if (wasLeased) Assert.NotNull(await queue.LeaseNextAsync("disappeared-worker", cancellationToken));
                clock.Advance(TimeSpan.FromMinutes(2));
            }
            else
            {
                // The next poll is the deterministic boundary: no wall-clock timeout is needed.
                stop.Cancel();
            }
        }, timeProvider: clock);

        var result = await client.ExecuteAsync<ListModelsJobOutput>(request, stop.Token);

        Assert.Equal(request.JobId, result.Receipt.JobId);
        Assert.Equal(2, process.EnsureCalls);
        Assert.Equal(LocalJobState.Succeeded,
            (await queue.GetStatusAsync(request.JobId, TestContext.Current.CancellationToken))!.State);
    }

    [Fact]
    public async Task Frequent_polls_do_not_repeat_the_process_check_until_its_interval_elapsed()
    {
        using var root = new TemporaryRuntimeRoot();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RecoveryClock();
        var queue = new DurableQueue(root.Path, clock);
        var process = new RecoveryProcess((_, _) => Task.CompletedTask);
        var polls = 0;
        var client = new BrokerClient(queue, process, (_, _) =>
        {
            if (++polls == 20)
            {
                Assert.Equal(1, process.EnsureCalls);
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            if (polls == 40) stop.Cancel();
            return Task.CompletedTask;
        }, timeProvider: clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync<ListModelsJobOutput>(
            LocalJobRequestFactory.CreateListModels("bounded-process-checks", LocalJobPriority.Interactive), stop.Token));

        Assert.Equal(2, process.EnsureCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_obeys_caller_cancellation_and_the_original_deadline(bool deadlineExpires)
    {
        using var root = new TemporaryRuntimeRoot();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RecoveryClock();
        var queue = new DurableQueue(root.Path, clock);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new RecoveryProcess(async (call, cancellationToken) =>
        {
            if (call == 1) return;
            entered.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var client = new BrokerClient(queue, process, (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(50));
            return Task.CompletedTask;
        }, timeout: TimeSpan.FromMinutes(1), timeProvider: clock);
        var request = LocalJobRequestFactory.CreateListModels("bounded-recovery", LocalJobPriority.Interactive);
        var pending = client.ExecuteAsync<ListModelsJobOutput>(request, stop.Token);
        try
        {
            var recoveryToken = await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (deadlineExpires)
            {
                // Only ten seconds remain, rather than a new minute granted by recovery.
                clock.Advance(TimeSpan.FromSeconds(10));
                Assert.True(recoveryToken.IsCancellationRequested);
            }
            else stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(LocalJobState.Queued,
                (await queue.GetStatusAsync(request.JobId, TestContext.Current.CancellationToken))!.State);
        }
        finally
        {
            stop.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private long _ticks;
        private readonly List<RecoveryTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration)
        {
            _ticks += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new RecoveryTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        private sealed class RecoveryTimer(RecoveryClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void FireIfDue()
            {
                if (_disposed || _due > clock._ticks) return;
                _due = long.MaxValue;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecoveryProcess(Func<int, CancellationToken, Task> ensure) : IBrokerProcess
    {
        public int EnsureCalls { get; private set; }
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            ensure(++EnsureCalls, cancellationToken);
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
