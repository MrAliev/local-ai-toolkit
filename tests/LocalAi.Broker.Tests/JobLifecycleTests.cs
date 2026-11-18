using System.Text.Json;
using LocalAi.Broker.Client;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class JobLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_clients_can_detach_reattach_and_cancel_only_the_owned_running_job()
    {
        using var runtime = new Runtime();
        var queue = new DurableQueue(runtime.Path, leaseDuration: TimeSpan.FromSeconds(5));
        var otherQueue = new DurableQueue(runtime.Path, leaseDuration: TimeSpan.FromSeconds(5));
        var process = new Process();
        var first = new BrokerClient(queue, process);
        var second = new BrokerClient(otherQueue, process);
        var one = await first.SubmitAsync(Request("one"), Ct);
        var two = await second.SubmitAsync(Request("two"), Ct);
        var entered = Signal();
        var cancellationSeen = Signal();
        var releaseExit = Signal();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        async Task<BrokerExecutionResult> Execute(LocalJobRequest request, IJobProgress _, CancellationToken token)
        {
            if (request.JobId != one.JobId)
                return new BrokerExecutionResult(JsonSerializer.SerializeToElement("two"));
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancellationSeen.TrySetResult();
                await releaseExit.Task.WaitAsync(Ct);
                throw;
            }
            throw new InvalidOperationException("Expected cancellation.");
        }
        var host = new BrokerHost(queue, "isolated-worker", Execute,
            idleInterval: TimeSpan.FromMilliseconds(5), heartbeatInterval: TimeSpan.FromMilliseconds(10));
        var running = host.RunAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(Ct);
            using var detach = new CancellationTokenSource();
            var abandonedWait = first.WaitAsync<string>(one.JobId, detach.Token);
            detach.Cancel();
            var detached = await Assert.ThrowsAsync<BrokerJobWaitCancelledException>(() => abandonedWait);
            Assert.Equal(one.JobId, detached.JobId);
            Assert.Equal(LocalJobState.Running, (await first.GetStatusAsync(one.JobId, Ct))!.State);
            Assert.False((await first.GetStatusAsync(one.JobId, Ct))!.CancellationRequested);
            Assert.Equal(LocalJobState.Queued, (await second.GetStatusAsync(two.JobId, Ct))!.State);

            var joined = await second.SubmitAsync(Request("one"), Ct);
            Assert.Equal(one.JobId, joined.JobId);
            Assert.True(joined.JoinedExisting);
            Assert.Null(joined.ControlToken);
            Assert.Equal(JobCancellationResult.NotOwner, await second.RequestCancellationAsync(joined, Ct));
            Assert.Equal(JobCancellationResult.NotOwner,
                await second.RequestCancellationAsync(one with { ControlToken = new string('0', 64) }, Ct));

            var reattached = first.WaitAsync<string>(one.JobId, Ct);
            Assert.Equal(JobCancellationResult.Requested, await first.RequestCancellationAsync(one, Ct));
            await cancellationSeen.Task.WaitAsync(Ct);
            var cancelling = await first.GetStatusAsync(one.JobId, Ct);
            Assert.Equal(LocalJobState.Running, cancelling!.State);
            Assert.True(cancelling.CancellationRequested);
            Assert.Equal(LocalJobState.Queued, (await second.GetStatusAsync(two.JobId, Ct))!.State);
            releaseExit.TrySetResult();
            await Assert.ThrowsAsync<BrokerJobCancelledException>(() => reattached);
            Assert.Equal("two", (await second.WaitAsync<string>(two.JobId, Ct)).Value);
            Assert.False(running.IsCompleted);
            Assert.Equal(JobCancellationResult.AlreadyTerminal, await first.RequestCancellationAsync(one, Ct));
        }
        finally
        {
            releaseExit.TrySetResult();
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Timeout_preserves_canonical_job_and_reattachment_does_not_resubmit()
    {
        using var runtime = new Runtime();
        var queue = new DurableQueue(runtime.Path);
        var process = new Process();
        var submitted = new List<BrokerJobSubmitted>();
        var observer = new Observer(submitted);
        var first = new BrokerClient(queue, process, observer: observer);
        var owned = await first.SubmitAsync(Request("same"), Ct);
        var shortWait = new BrokerClient(queue, process, timeout: TimeSpan.FromMilliseconds(50), observer: observer);
        var timeout = await Assert.ThrowsAsync<BrokerJobWaitTimeoutException>(() =>
            shortWait.ExecuteAsync<string>(Request("same"), Ct));
        Assert.Equal(owned.JobId, timeout.JobId);
        Assert.Equal(2, submitted.Count);
        Assert.All(submitted, item => Assert.Equal(owned.JobId, item.JobId));
        Assert.True(submitted[1].JoinedExisting);
        Assert.Equal(LocalJobState.Queued, (await first.GetStatusAsync(owned.JobId, Ct))!.State);
        var lease = await queue.LeaseNextAsync("worker", Ct);
        await Complete(queue, lease!, "answer");
        Assert.Equal("answer", (await first.WaitAsync<string>(owned.JobId, Ct)).Value);
        Assert.Null(await queue.LeaseNextAsync("worker", Ct));
    }

    [Fact]
    public async Task Queued_cancel_and_terminal_completion_are_serialized_and_legacy_jobs_fail_closed()
    {
        using var runtime = new Runtime();
        var queue = new DurableQueue(runtime.Path);
        var owned = await queue.EnqueueOwnedAsync(Request("queued"), Ct);
        var jobDirectory = System.IO.Path.Combine(runtime.Path, "jobs", owned.JobId.ToString("N"));
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(jobDirectory, "state.json"), Ct));
        Assert.Equal(14, state.RootElement.EnumerateObject().Count());
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(jobDirectory, "request.json"), Ct));
        Assert.False(request.RootElement.GetProperty("Request").TryGetProperty("ControlToken", out _));
        Assert.DoesNotContain(owned.ControlToken!, await File.ReadAllTextAsync(System.IO.Path.Combine(jobDirectory, "control.json"), Ct));
        Assert.DoesNotContain(owned.ControlToken!, owned.ToString());
        Assert.Equal(JobCancellationResult.Requested, await queue.RequestCancellationAsync(owned, Ct));
        Assert.Equal(LocalJobState.Cancelled, (await queue.GetStatusAsync(owned.JobId, Ct))!.State);
        Assert.Null(await queue.LeaseNextAsync("worker", Ct));

        var legacyRequest = Request("legacy");
        await queue.EnqueueAsync(legacyRequest, Ct);
        Assert.Equal(JobCancellationResult.NotOwner, await queue.RequestCancellationAsync(
            new BrokerJobHandle(legacyRequest.JobId, false, owned.ControlToken), Ct));
        Assert.Equal(LocalJobState.Queued, (await queue.GetStatusAsync(legacyRequest.JobId, Ct))!.State);
        var legacyLease = await queue.LeaseNextAsync("worker", Ct);
        await Complete(queue, legacyLease!, "legacy");

        var completed = await queue.EnqueueOwnedAsync(Request("completed"), Ct);
        var lease = await queue.LeaseNextAsync("worker", Ct);
        await Complete(queue, lease!, "finished");
        Assert.Equal(JobCancellationResult.AlreadyTerminal, await queue.RequestCancellationAsync(completed, Ct));
        Assert.Equal(LocalJobState.Succeeded, (await queue.GetStatusAsync(completed.JobId, Ct))!.State);
        Assert.Equal(JobCancellationResult.NotFound, await queue.RequestCancellationAsync(
            completed with { JobId = Guid.NewGuid() }, Ct));

        // Request acceptance is cooperative, not a promise to undo an executor which finishes.
        // Whichever terminal write wins the queue mutex remains the final result.
        var race = await queue.EnqueueOwnedAsync(Request("completion-race"), Ct);
        var racingLease = await queue.LeaseNextAsync("worker", Ct);
        Assert.Equal(JobCancellationResult.Requested, await queue.RequestCancellationAsync(race, Ct));
        await Complete(queue, racingLease!, "won-completion");
        var raced = await queue.GetStatusAsync(race.JobId, Ct);
        Assert.Equal(LocalJobState.Succeeded, raced!.State);
        Assert.True(raced.CancellationRequested);
        Assert.Equal(JobCancellationResult.AlreadyTerminal, await queue.RequestCancellationAsync(race, Ct));
    }

    [Fact]
    public async Task Failed_private_handle_sink_leaves_the_accepted_job_durable()
    {
        using var runtime = new Runtime();
        var queue = new DurableQueue(runtime.Path);
        BrokerJobHandle? captured = null;
        var calls = 0;
        var client = new BrokerClient(queue, new Process(), onSubmitted: handle =>
        {
            calls++;
            captured = handle;
            throw new IOException("private receipt could not be saved");
        });
        var failure = await Assert.ThrowsAsync<BrokerJobSubmissionNotificationException>(() =>
            client.ExecuteAsync<string>(Request("sink"), Ct));
        Assert.Equal(1, calls);
        Assert.Equal(captured!.JobId, failure.JobId);
        Assert.Equal(LocalJobState.Queued, (await queue.GetStatusAsync(failure.JobId, Ct))!.State);
        Assert.False((await queue.GetStatusAsync(failure.JobId, Ct))!.CancellationRequested);
        var unsupported = new BrokerClient(queue, new LegacyProcess());
        Assert.Equal(JobCancellationResult.Unsupported, await unsupported.RequestCancellationAsync(captured, Ct));
        Assert.Equal(JobCancellationResult.Requested, await queue.RequestCancellationAsync(captured, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_cancellation_survives_an_expired_lease_without_restarting_the_executor(bool targetCancelledJob)
    {
        using var runtime = new Runtime();
        var clock = new Clock();
        var queue = new DurableQueue(runtime.Path, timeProvider: clock, leaseDuration: TimeSpan.FromSeconds(5));
        var cancelled = await queue.EnqueueOwnedAsync(Request("lost-worker-cancel"), Ct);
        var other = await queue.EnqueueOwnedAsync(Request("replacement-work"), Ct);
        var abandonedLease = await queue.LeaseNextAsync("lost-worker", Ct);
        Assert.Equal(cancelled.JobId, abandonedLease!.Request.JobId);
        Assert.Equal(JobCancellationResult.Requested, await queue.RequestCancellationAsync(cancelled, Ct));
        Assert.Equal(LocalJobState.Running, (await queue.GetStatusAsync(cancelled.JobId, Ct))!.State);

        clock.Advance(TimeSpan.FromSeconds(6));
        var recovered = new DurableQueue(runtime.Path, timeProvider: clock, leaseDuration: TimeSpan.FromSeconds(5));
        if (targetCancelledJob)
            Assert.Null(await recovered.TryLeaseAsync(cancelled.JobId, "replacement-worker", Ct));
        var cancelledExecutions = 0;
        Task<BrokerExecutionResult> Execute(LocalJobRequest request, IJobProgress _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.JobId == cancelled.JobId) cancelledExecutions++;
            return Task.FromResult(new BrokerExecutionResult(JsonSerializer.SerializeToElement("replacement-answer")));
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var host = new BrokerHost(recovered, "replacement-worker", Execute,
            idleInterval: TimeSpan.FromMilliseconds(5), heartbeatInterval: TimeSpan.FromMilliseconds(10));
        var running = host.RunAsync(stop.Token);
        try
        {
            var client = new BrokerClient(recovered, new Process());
            Assert.Equal("replacement-answer", (await client.WaitAsync<string>(other.JobId, Ct)).Value);
            Assert.Equal(0, cancelledExecutions);
            var terminal = await recovered.GetStatusAsync(cancelled.JobId, Ct);
            Assert.Equal(LocalJobState.Cancelled, terminal!.State);
            Assert.True(terminal.CancellationRequested);
            Assert.Equal(JobCancellationResult.AlreadyTerminal, await recovered.RequestCancellationAsync(cancelled, Ct));
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateListModels(key, LocalJobPriority.Interactive);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Complete(DurableQueue queue, LeasedJob lease, string value)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new BrokerResponseEnvelope(JsonSerializer.SerializeToElement(value),
            ReceiptFactory.Create(lease.Request, now, now));
        await queue.CompleteAsync(lease.Request.JobId, lease.WorkerId, lease.LeaseId,
            JsonSerializer.SerializeToElement(envelope, LocalAiJson.Strict), Ct);
    }

    private sealed class Observer(List<BrokerJobSubmitted> submissions) : ILocalRunObserver
    {
        public void Report(LocalRunStep step)
        {
            if (step is BrokerJobSubmitted submitted) submissions.Add(submitted);
        }
    }

    private sealed class Process : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool SupportsJobCancellation() => true;
    }

    private sealed class LegacyProcess : IBrokerProcess
    {
        public Task EnsureRunningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Runtime : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "localai-lifecycle-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
