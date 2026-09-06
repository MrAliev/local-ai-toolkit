using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// A position is an encouragement, not a fact the job depends on — and the sink the host hands
/// the executor says so in its own summary: "a queue that refused one must never turn a running
/// download into a failed job". It had no construction that made that true. Everything
/// `DurableQueue.ReportProgressAsync` can throw — `LeaseLostException` from the two lease checks,
/// `IOException` from its own atomic write — went straight out of the executor and into the
/// host's catch-all, which records the job as Failed.
///
/// The shape that matters is a long model pull: one `File.Move` under the progress write throws
/// an `IOException`, and the download is reported as failed while it is still running.
/// </summary>
public sealed class ProgressCannotFailAJobTests
{
    /// <summary>
    /// Long enough that no correct run can reach it, and it is a guard against a hang rather than
    /// a measurement: nothing here waits for anything but the one job to finish.
    /// </summary>
    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_refused_progress_report_leaves_the_job_it_came_from_succeeded()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var enqueued = await queue.EnqueueAsync(Request("reports-then-succeeds"), Ct);
        var refusing = new RefusesEveryProgressReport(queue);
        using var stop = new CancellationTokenSource(Guard);

        async Task<BrokerExecutionResult> Execute(
            LocalJobRequest request,
            IJobProgress progress,
            CancellationToken cancellationToken)
        {
            await progress.ReportAsync(
                new JobProgress("Downloading", Detail: null, Completed: 70, Total: 100),
                cancellationToken);
            return new BrokerExecutionResult(
                JsonSerializer.SerializeToElement(new { request.JobId }));
        }

        var host = new BrokerHost(
            refusing,
            "progress-worker",
            Execute,
            idleDelay: StopWhenTheQueueRunsDry(stop),
            idleInterval: TimeSpan.FromMilliseconds(5),
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));

        var diagnostic = await queue.GetDiagnosticAsync(enqueued.JobId, Ct);
        Assert.NotNull(diagnostic);
        Assert.Equal(LocalJobState.Succeeded, diagnostic.State);
        Assert.Null(diagnostic.FailureCode);
        Assert.True(refusing.Refused, "the fake never got asked to report progress");
    }

    /// <summary>
    /// Cancellation is the one thing the sink must not swallow: a host shutting down cancels the
    /// attempt, and an executor that reports progress on the way out has to feel it. Swallowing it
    /// would turn a cancelled job into one that runs to completion against a dead lease.
    /// </summary>
    [Fact]
    public async Task Cancellation_still_reaches_the_executor_through_the_sink()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        await queue.EnqueueAsync(Request("cancelled-while-reporting"), Ct);
        var cancelling = new CancelsEveryProgressReport(queue);
        using var stop = new CancellationTokenSource(Guard);
        var seen = false;

        async Task<BrokerExecutionResult> Execute(
            LocalJobRequest request,
            IJobProgress progress,
            CancellationToken cancellationToken)
        {
            try
            {
                await progress.ReportAsync(
                    new JobProgress("Downloading", Detail: null, Completed: 1, Total: 2),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                seen = true;
            }

            return new BrokerExecutionResult(
                JsonSerializer.SerializeToElement(new { request.JobId }));
        }

        var host = new BrokerHost(
            cancelling,
            "cancel-worker",
            Execute,
            idleDelay: StopWhenTheQueueRunsDry(stop),
            idleInterval: TimeSpan.FromMilliseconds(5),
            heartbeatInterval: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(stop.Token));

        Assert.True(seen, "cancellation was swallowed by the progress sink");
    }

    /// <summary>
    /// Stops the host on a condition rather than after a duration: the host idles only once it
    /// has nothing left to lease, so the first idle means the one job is over — however it ended.
    /// Waiting out the guard instead would turn a regression into a two-minute test.
    /// </summary>
    private static Func<TimeSpan, CancellationToken, Task> StopWhenTheQueueRunsDry(
        CancellationTokenSource stop) =>
        (_, _) =>
        {
            stop.Cancel();
            return Task.CompletedTask;
        };

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(
            key,
            LocalJobPriority.Foreground,
            "test-model",
            ["input"]);

    /// <summary>
    /// Everything but the progress report is the real queue, so the record this test reads is the
    /// one the product would have written.
    /// </summary>
    private sealed class RefusesEveryProgressReport(IBrokerQueue inner) : IBrokerQueue
    {
        public bool Refused { get; private set; }

        public TimeSpan LeaseDuration => inner.LeaseDuration;

        public Task ReportProgressAsync(
            Guid jobId,
            string workerId,
            Guid leaseId,
            JobProgress progress,
            CancellationToken cancellationToken = default)
        {
            Refused = true;
            throw new IOException("The process cannot access the file because it is being used.");
        }

        public Task<LeasedJob?> LeaseNextAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.LeaseNextAsync(workerId, cancellationToken);

        public Task<LeaseHeartbeat> HeartbeatAsync(Guid jobId, string workerId, Guid leaseId, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(jobId, workerId, leaseId, cancellationToken);

        public Task CompleteAsync(Guid jobId, string workerId, Guid leaseId, JsonElement body, CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(jobId, workerId, leaseId, body, cancellationToken);

        public Task FailAsync(Guid jobId, string workerId, Guid leaseId, string failureCode, string? failureReason, CancellationToken cancellationToken = default) =>
            inner.FailAsync(jobId, workerId, leaseId, failureCode, failureReason, cancellationToken);

        public Task CancelAsync(Guid jobId, string workerId, Guid leaseId, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(jobId, workerId, leaseId, cancellationToken);
    }

    private sealed class CancelsEveryProgressReport(IBrokerQueue inner) : IBrokerQueue
    {
        public TimeSpan LeaseDuration => inner.LeaseDuration;

        public Task ReportProgressAsync(
            Guid jobId,
            string workerId,
            Guid leaseId,
            JobProgress progress,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

        public Task<LeasedJob?> LeaseNextAsync(string workerId, CancellationToken cancellationToken = default) =>
            inner.LeaseNextAsync(workerId, cancellationToken);

        public Task<LeaseHeartbeat> HeartbeatAsync(Guid jobId, string workerId, Guid leaseId, CancellationToken cancellationToken = default) =>
            inner.HeartbeatAsync(jobId, workerId, leaseId, cancellationToken);

        public Task CompleteAsync(Guid jobId, string workerId, Guid leaseId, JsonElement body, CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(jobId, workerId, leaseId, body, cancellationToken);

        public Task FailAsync(Guid jobId, string workerId, Guid leaseId, string failureCode, string? failureReason, CancellationToken cancellationToken = default) =>
            inner.FailAsync(jobId, workerId, leaseId, failureCode, failureReason, cancellationToken);

        public Task CancelAsync(Guid jobId, string workerId, Guid leaseId, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(jobId, workerId, leaseId, cancellationToken);
    }
}
