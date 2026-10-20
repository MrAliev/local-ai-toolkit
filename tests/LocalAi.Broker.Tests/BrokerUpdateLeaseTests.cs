using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Broker;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class BrokerUpdateLeaseTests
{
    [Fact]
    public async Task Work_arriving_after_the_download_precheck_is_rechecked_before_activation()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(0, await queue.CountPendingAsync(token));
        // The package download does not pause other clients. This is the request that
        // arrives after the initial CLI check but before the verified package is activated.
        var arriving = Request("arrived-during-download");
        await new DurableQueue(root.Path).EnqueueAsync(arriving, token);

        var failure = await Assert.ThrowsAsync<BrokerUpdateBusyException>(() =>
            queue.AcquireUpdateLeaseAsync(false, TimeSpan.FromMinutes(1), cancellationToken: token));

        Assert.Equal(1, failure.PendingJobs);
        Assert.Equal(LocalJobState.Queued, (await queue.GetStatusAsync(arriving.JobId, token))!.State);
    }

    [Fact]
    public async Task A_running_job_is_busy_even_when_the_waiting_queue_is_empty()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(Request("running"), token);
        Assert.NotNull(await queue.LeaseNextAsync("worker", token));

        var failure = await Assert.ThrowsAsync<BrokerUpdateBusyException>(() =>
            queue.AcquireUpdateLeaseAsync(false, TimeSpan.FromMinutes(1), cancellationToken: token));

        Assert.Equal(1, failure.PendingJobs);
        // A refused update restores admission as well.
        Assert.False((await queue.EnqueueAsync(Request("after-refusal"), token)).JoinedExisting);
    }

    [Fact]
    public async Task Admission_pauses_but_heartbeats_and_existing_work_can_finish()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var otherClient = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(Request("running"), token);
        var running = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker", token));
        var draining = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisition = queue.AcquireUpdateLeaseAsync(
            true, TimeSpan.FromMinutes(1), count => draining.TrySetResult(count), token);
        Assert.Equal(1, await draining.Task.WaitAsync(token));

        await Assert.ThrowsAsync<BrokerUpdateBusyException>(() =>
            otherClient.EnqueueAsync(Request("new-work"), token));
        await otherClient.HeartbeatAsync(running.Request.JobId, "worker", running.LeaseId, token);
        await otherClient.CompleteAsync(
            running.Request.JobId, "worker", running.LeaseId,
            JsonSerializer.SerializeToElement(new { ok = true }), token);
        using (await acquisition)
        {
            await Assert.ThrowsAsync<BrokerUpdateBusyException>(() =>
                otherClient.EnqueueAsync(Request("during-activation"), token));
        }

        Assert.False((await otherClient.EnqueueAsync(Request("after-activation"), token)).JoinedExisting);
    }

    [Fact]
    public async Task The_final_lease_also_excludes_clients_using_the_original_queue_mutex()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        using var lease = await queue.AcquireUpdateLeaseAsync(
            false, TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);
        using var originalMutex = new Mutex(false, OriginalQueueMutexName(root.Path));
        var entered = originalMutex.WaitOne(0);
        if (entered)
        {
            originalMutex.ReleaseMutex();
        }

        Assert.False(entered);
    }

    [Fact]
    public async Task A_second_updater_is_refused_without_waiting_on_the_activation_mutex()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;
        using var lease = await queue.AcquireUpdateLeaseAsync(
            false, TimeSpan.FromMinutes(1), cancellationToken: token);

        await Assert.ThrowsAsync<BrokerUpdateBusyException>(() =>
            new DurableQueue(root.Path).AcquireUpdateLeaseAsync(
                false, TimeSpan.FromMinutes(1), cancellationToken: token));
    }

    [Fact]
    public async Task An_abandoned_admission_mutex_does_not_disable_future_submissions()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        using var abandoned = new Mutex(false, OriginalQueueMutexName(root.Path) + ".Update");
        var owner = new Thread(() => abandoned.WaitOne());
        owner.Start();
        owner.Join();
        // The owner's thread has exited without releasing its mutex, just as happens
        // when the process hosting an updater terminates during its drain.

        Assert.False((await queue.EnqueueAsync(
            Request("after-owner-exit"), TestContext.Current.CancellationToken)).JoinedExisting);
    }

    [Fact]
    public async Task Cancellation_while_draining_restores_admission()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(Request("existing"), token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisition = queue.AcquireUpdateLeaseAsync(
            true, TimeSpan.FromMinutes(1), _ => draining.TrySetResult(), cancellation.Token);
        await draining.Task.WaitAsync(token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);

        Assert.False((await queue.EnqueueAsync(Request("after-cancellation"), token)).JoinedExisting);
    }

    [Fact]
    public async Task An_activation_failure_releases_the_gate()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = await queue.AcquireUpdateLeaseAsync(
                false, TimeSpan.FromMinutes(1), cancellationToken: token);
            throw new InvalidOperationException("synthetic activation failure");
        });

        Assert.False((await queue.EnqueueAsync(Request("after-failure"), token)).JoinedExisting);
    }

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(key, LocalJobPriority.Background, "test-model", ["input"]);

    private static string OriginalQueueMutexName(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return (OperatingSystem.IsWindows() ? @"Global\" : string.Empty) +
            "LocalAi.Broker." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
