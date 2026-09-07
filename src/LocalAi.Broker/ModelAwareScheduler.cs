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
    private readonly TimeSpan _modelHold;
    private readonly Queue<Guid> _snapshot = [];
    private string? _snapshotModel;
    private DateTimeOffset? _gatherDeadline;

    /// <param name="modelHold">
    /// How long a loaded model is held before work for another one takes the worker. The
    /// operator sets it as the idle keep-alive, because it is the same question asked twice.
    /// Raised to <see cref="GatherWindow"/> when it is shorter: the keep-alive defaults to
    /// zero, and zero as a hold would switch the instant a queue empties.
    /// </param>
    public ModelAwareScheduler(TimeProvider? timeProvider = null, TimeSpan? modelHold = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _modelHold = modelHold is { } hold && hold > GatherWindow ? hold : GatherWindow;
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
        var selectedModel = starved?.Model ?? jobs
            .GroupBy(candidate => candidate.Model, StringComparer.Ordinal)
            .OrderByDescending(group =>
                residentModel is not null &&
                string.Equals(group.Key, residentModel, StringComparison.Ordinal))
            .ThenBy(group => group.Min(candidate => candidate.PredictedDuration))
            .ThenBy(group => group.Min(candidate => candidate.CreatedAtUtc))
            .ThenBy(group => group.Min(candidate => candidate.Priority))
            .ThenBy(group => group.Min(candidate => candidate.Sequence))
            .First()
            .Key;

        var group = jobs
            .Where(candidate => string.Equals(
                candidate.Model,
                selectedModel,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.PredictedDuration)
            .ThenBy(candidate => candidate.Priority)
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
            // Leaving a model that is loaded costs an unload and a reload — ModelRuntime
            // unloads every other managed model before it preflights — so that wait is the
            // operator's keep-alive. With nothing loaded there is nothing to protect, and
            // gathering more work before committing to a long job is a different question
            // with a different answer; both keep the two seconds.
            var window = switchesModel && residentModel is not null
                ? _modelHold
                : GatherWindow;
            _gatherDeadline ??= now + window;
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
