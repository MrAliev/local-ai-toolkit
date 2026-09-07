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
    /// An index build feeds the queue in batches, and between two of them there is a moment when
    /// no embedding work is queued at all. Two seconds was enough for a triage call to win that
    /// moment, and winning it costs the build its model: ModelRuntime.EnsureReadyAsync unloads
    /// every other managed model before it preflights, so the next batch reloads eight
    /// quantised billion parameters from disk. Five such gaps in one build on the owner's
    /// machine, and the shortest cost 108 seconds.
    ///
    /// So the hold before leaving a loaded model is the keep-alive the operator set for exactly
    /// this question, not a constant. Nothing waits for ever: the fifteen-minute starvation
    /// guard is unchanged, and a hold shorter than the gather window is raised to it.
    /// </summary>
    [Fact]
    public void The_resident_model_is_held_for_the_keep_alive_before_another_takes_the_worker()
    {
        var scheduler = new ModelAwareScheduler(_time, TimeSpan.FromSeconds(30));
        var cold = Job(1, "cold", seconds: 1);

        Assert.Null(scheduler.Decide([cold], residentModel: "resident").JobId);

        // Past the old two-second window, and well short of the keep-alive.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(scheduler.Decide([cold], residentModel: "resident").JobId);

        // And it is a hold, not a refusal: once the model really has gone quiet, the cold job
        // runs rather than starving behind a model nothing is using.
        _time.Advance(TimeSpan.FromSeconds(21));
        Assert.Equal(cold.JobId, scheduler.Decide([cold], residentModel: "resident").JobId);
    }

    /// <summary>
    /// The keep-alive defaults to zero, which as a hold would mean switching on the instant the
    /// queue empties — worse than what was there before. The gather window is a floor.
    /// </summary>
    [Fact]
    public void A_keep_alive_below_the_gather_window_does_not_shorten_it()
    {
        var scheduler = new ModelAwareScheduler(_time, TimeSpan.Zero);
        var cold = Job(1, "cold", seconds: 1);

        Assert.Null(scheduler.Decide([cold], residentModel: "resident").JobId);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(scheduler.Decide([cold], residentModel: "resident").JobId);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(cold.JobId, scheduler.Decide([cold], residentModel: "resident").JobId);
    }

    private ScheduledJobCandidate Job(
        long sequence,
        string model,
        int seconds,
        DateTimeOffset? createdAtUtc = null,
        bool dependencyReady = true) =>
        new(
            JobId(sequence),
            sequence,
            LocalJobPriority.Foreground,
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
