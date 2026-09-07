using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ScheduledJobCandidate(
    Guid JobId,
    long Sequence,
    LocalJobPriority Priority,
    DateTimeOffset CreatedAtUtc,
    string Model,
    TimeSpan PredictedDuration,
    LocalDurationClass DurationClass,
    bool IsDependencyReady,
    bool IsMaintenance = false);

public sealed record ScheduleDecision(
    Guid? JobId,
    string? Model,
    DateTimeOffset? WaitUntilUtc)
{
    public static ScheduleDecision Empty { get; } = new(null, null, null);
}

public sealed class ModelAwareScheduler
{
    private static readonly TimeSpan GatherWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StarvationAge = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _timeProvider;
    private readonly Queue<Guid> _snapshot = [];
    private string? _snapshotModel;
    private DateTimeOffset? _gatherDeadline;

    public ModelAwareScheduler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ScheduleDecision Decide(
        IReadOnlyList<ScheduledJobCandidate> candidates,
        string? residentModel)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var queued = candidates.ToDictionary(candidate => candidate.JobId);
        var inferenceIsQueued = queued.Values.Any(
            candidate => !candidate.IsMaintenance);
        var eligible = queued.Values
            .Where(candidate => candidate.IsDependencyReady)
            .ToDictionary(candidate => candidate.JobId);
        if (inferenceIsQueued)
        {
            eligible = eligible
                .Where(pair => !pair.Value.IsMaintenance)
                .ToDictionary();
        }

        // The snapshot is a decision already taken, and it must not outrank the reason to take
        // one again. Work someone is waiting on arrives after a group was frozen, and every job
        // left in the snapshot is one it would otherwise wait behind — a build's snapshot is
        // every batch it has queued.
        if (_snapshot.Count > 0 && eligible.Count > 0)
        {
            var mostUrgent = eligible.Values.Min(candidate => candidate.Priority);
            var outranked = _snapshot
                .Where(eligible.ContainsKey)
                .Any(jobId => eligible[jobId].Priority > mostUrgent);
            if (outranked)
            {
                _snapshot.Clear();
                _snapshotModel = null;
            }
        }

        while (_snapshot.Count > 0)
        {
            var jobId = _snapshot.Dequeue();
            if (eligible.ContainsKey(jobId))
            {
                return new ScheduleDecision(jobId, _snapshotModel, null);
            }
        }

        _snapshotModel = null;
        if (eligible.Count == 0)
        {
            _gatherDeadline = null;
            return ScheduleDecision.Empty;
        }

        var now = _timeProvider.GetUtcNow();
        var jobs = eligible.Values.ToArray();
        var starved = jobs
            .Where(candidate => now - candidate.CreatedAtUtc >= StarvationAge)
            .OrderBy(candidate => candidate.CreatedAtUtc)
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Sequence)
            .FirstOrDefault();
        // Priority first, and affinity second. An index build is thousands of Background jobs
        // for one model, so affinity read first meant a Foreground question waited out the
        // build: every batch won against it, one at a time, for as long as the build fed the
        // queue. Priority is the one field that says a person is waiting for this answer.
        //
        // Reading it first buys an unload and a reload — ModelRuntime clears the other managed
        // models before it preflights — and that is the trade being made on purpose, not an
        // oversight. Among work of equal urgency, affinity still decides, and then the shortest
        // job, so nothing pays for a swap that nobody was waiting on.
        var selectedModel = starved?.Model ?? jobs
            .GroupBy(candidate => candidate.Model, StringComparer.Ordinal)
            .OrderBy(group => group.Min(candidate => candidate.Priority))
            .ThenByDescending(group =>
                residentModel is not null &&
                string.Equals(group.Key, residentModel, StringComparison.Ordinal))
            .ThenBy(group => group.Min(candidate => candidate.PredictedDuration))
            .ThenBy(group => group.Min(candidate => candidate.CreatedAtUtc))
            .ThenBy(group => group.Min(candidate => candidate.Sequence))
            .First()
            .Key;

        var group = jobs
            .Where(candidate => string.Equals(
                candidate.Model,
                selectedModel,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.PredictedDuration)
            .ThenBy(candidate => candidate.Sequence)
            .ToArray();
        var switchesModel =
            !string.Equals(selectedModel, residentModel, StringComparison.Ordinal);
        var requiresGatherWindow =
            starved is null &&
            (switchesModel ||
             group.Any(candidate => candidate.DurationClass == LocalDurationClass.Long));
        if (requiresGatherWindow)
        {
            // Two seconds, and deliberately not the idle keep-alive. The keep-alive says how
            // long a model is worth holding when nothing wants it; a queued job for another
            // model is the answer to whether anything wants it, so letting the keep-alive gate
            // this would turn "keep it a minute" into "wait a minute". This window is only for
            // gathering work that is on its way, and it is short for that reason.
            _gatherDeadline ??= now + GatherWindow;
            if (now < _gatherDeadline)
            {
                return new ScheduleDecision(null, selectedModel, _gatherDeadline);
            }
        }

        _gatherDeadline = null;
        _snapshotModel = selectedModel;
        foreach (var candidate in group)
        {
            _snapshot.Enqueue(candidate.JobId);
        }

        return new ScheduleDecision(_snapshot.Dequeue(), _snapshotModel, null);
    }
}
