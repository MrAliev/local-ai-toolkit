using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class ModelAwareSchedulerTests
{
    private readonly ManualTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 29, 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Frozen_snapshot_uses_shortest_job_first_and_excludes_late_arrivals()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var first = Job(1, "model-a", seconds: 8);
        var second = Job(2, "model-a", seconds: 3);

        var start = scheduler.Decide([first, second], residentModel: "model-a");
        Assert.Equal(second.JobId, start.JobId);

        var late = Job(3, "model-a", seconds: 1);
        var next = scheduler.Decide([first, second, late], residentModel: "model-a");
        Assert.Equal(first.JobId, next.JobId);

        var recalculated = scheduler.Decide([late], residentModel: "model-a");
        Assert.Equal(late.JobId, recalculated.JobId);
    }

    [Fact]
    public void Resident_model_affinity_wins_before_shorter_cold_work()
    {
        var scheduler = new ModelAwareScheduler(_time);

        var decision = scheduler.Decide(
            [
                Job(1, "resident", seconds: 8),
                Job(2, "cold", seconds: 1)
            ],
            residentModel: "resident");

        Assert.Equal("resident", decision.Model);
        Assert.Equal(JobId(1), decision.JobId);
    }

    [Fact]
    public void Model_switch_opens_one_two_second_related_work_window()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var candidate = Job(1, "cold", seconds: 3);

        var waiting = scheduler.Decide([candidate], residentModel: "other");

        Assert.Null(waiting.JobId);
        Assert.Equal(_time.GetUtcNow().AddSeconds(2), waiting.WaitUntilUtc);

        _time.Advance(TimeSpan.FromSeconds(2));
        var ready = scheduler.Decide(
            [candidate, Job(2, "cold", seconds: 1)],
            residentModel: "other");
        Assert.Equal(JobId(2), ready.JobId);
    }

    [Fact]
    public void Fifteen_minute_age_forces_the_next_compatible_snapshot()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var starved = Job(
            1,
            "cold",
            seconds: 30,
            createdAtUtc: _time.GetUtcNow().AddMinutes(-15));
        var resident = Job(2, "resident", seconds: 1);

        var decision = scheduler.Decide(
            [resident, starved],
            residentModel: "resident");

        Assert.Equal("cold", decision.Model);
        Assert.Equal(starved.JobId, decision.JobId);
    }

    [Fact]
    public void Dependency_not_ready_is_not_scheduled()
    {
        var scheduler = new ModelAwareScheduler(_time);

        var decision = scheduler.Decide(
            [
                Job(1, "model", seconds: 1, dependencyReady: false),
                Job(2, "model", seconds: 2)
            ],
            residentModel: "model");

        Assert.Equal(JobId(2), decision.JobId);
    }

    [Fact]
    public void Maintenance_waits_until_no_inference_job_is_queued()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var maintenance = Job(1, "maintenance", seconds: 1) with
        {
            IsMaintenance = true
        };
        var inference = Job(2, "model", seconds: 20);

        var decision = scheduler.Decide(
            [maintenance, inference],
            residentModel: "model");

        Assert.Equal(inference.JobId, decision.JobId);
    }

    [Fact]
    public void Maintenance_waits_for_dependency_blocked_inference_work()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var maintenance = Job(1, "maintenance", seconds: 1) with
        {
            IsMaintenance = true
        };
        var blockedInference = Job(
            2,
            "model",
            seconds: 20,
            dependencyReady: false);

        var decision = scheduler.Decide(
            [maintenance, blockedInference],
            residentModel: "maintenance");

        Assert.Null(decision.JobId);
    }

    /// <summary>
    /// A build owns the loaded model for as long as it keeps feeding the queue, and somebody
    /// else is waiting. Model affinity used to decide this, so the person waiting waited: an
    /// index build is thousands of Background jobs, and each one won on affinity against a
    /// Foreground question that would have taken a minute.
    ///
    /// Priority is what says somebody is waiting, and it is now read before affinity. The swap
    /// costs an unload and a reload — ModelRuntime unloads the other managed models before it
    /// preflights — and that cost is the point rather than an oversight: paying it once beats
    /// making an interactive tool wait out a build.
    /// </summary>
    [Fact]
    public void Interactive_work_is_not_queued_behind_a_build_that_owns_the_model()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var build = Job(1, "embed", seconds: 8, priority: LocalJobPriority.Background);
        var asked = Job(2, "chat", seconds: 30, priority: LocalJobPriority.Foreground);

        // Switching models still gathers for a moment, in case more of the same arrives.
        Assert.Null(scheduler.Decide([build, asked], residentModel: "embed").JobId);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(
            asked.JobId,
            scheduler.Decide([build, asked], residentModel: "embed").JobId);
    }

    /// <summary>
    /// Nothing longer than the gather window stands between a queued job and the model it
    /// needs. The keep-alive says how long a model is worth holding when nothing wants it, and
    /// a queued job for another model is the answer to whether anything wants it — so it is not
    /// a lease, and the scheduler does not read it. That it is not wired in is a property of
    /// the broker's entry point, which no test builds; what is pinned here is the window.
    /// </summary>
    [Fact]
    public void Nothing_beyond_the_gather_window_holds_the_model_a_queued_job_needs()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var cold = Job(1, "cold", seconds: 1);

        Assert.Null(scheduler.Decide([cold], residentModel: "resident").JobId);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(cold.JobId, scheduler.Decide([cold], residentModel: "resident").JobId);
    }

    /// <summary>
    /// The snapshot exists so a chosen model's work is drained rather than re-decided job by
    /// job. It must not outrank the reason for choosing again: a build's remaining batches are
    /// in it, and whoever just asked a question would wait out every one of them.
    /// </summary>
    [Fact]
    public void A_frozen_snapshot_is_dropped_when_work_someone_is_waiting_on_arrives()
    {
        var scheduler = new ModelAwareScheduler(_time);
        var first = Job(1, "embed", seconds: 8, priority: LocalJobPriority.Background);
        var second = Job(2, "embed", seconds: 9, priority: LocalJobPriority.Background);

        Assert.Equal(
            first.JobId,
            scheduler.Decide([first, second], residentModel: "embed").JobId);

        var asked = Job(3, "chat", seconds: 30, priority: LocalJobPriority.Foreground);
        Assert.Null(scheduler.Decide([second, asked], residentModel: "embed").JobId);
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(
            asked.JobId,
            scheduler.Decide([second, asked], residentModel: "embed").JobId);
    }

    private ScheduledJobCandidate Job(
        long sequence,
        string model,
        int seconds,
        DateTimeOffset? createdAtUtc = null,
        bool dependencyReady = true,
        LocalJobPriority priority = LocalJobPriority.Foreground) =>
        new(
            JobId(sequence),
            sequence,
            priority,
            createdAtUtc ?? _time.GetUtcNow(),
            model,
            TimeSpan.FromSeconds(seconds),
            LocalDurationClass.Short,
            dependencyReady);

    private static Guid JobId(long value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
    }
}
