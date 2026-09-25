using System.Collections;
using System.Reflection;
using System.Text.Json;
using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed record ModelTelemetryRecord(
    Guid JobId,
    LocalTaskProfile TaskProfile,
    string Model,
    int ContextTokens,
    LocalSizeBucket Input,
    LocalSizeBucket Output,
    bool WasCold,
    bool ModelSwitched,
    bool UsedFallback,
    string ValidatorResult,
    ModelExecutionOutcome Outcome,
    TimeSpan QueueDuration,
    TimeSpan LoadDuration,
    TimeSpan ExecutionDuration,
    TimeSpan TotalDuration,
    long EstimatedGrossCloudTokensSaved,
    long EstimatedVerificationTokens,
    long EstimatedNetCloudTokensSaved,
    string CatalogVersion,
    DateTimeOffset RecordedAtUtc)
{
    public int SchemaVersion { get; init; } = 1;
    public Guid? AttemptId { get; init; }
    public Guid? WorkflowId { get; init; }
    public long? InputCharacters { get; init; }
    public long? OutputCharacters { get; init; }
    public long? BackendInputTokens { get; init; }
    public long? BackendOutputTokens { get; init; }
    public UsageValue CloudTokenSavings { get; init; } = UsageValue.Unknown("legacy_comparison_unavailable");
    public ResidencyShortfall? Residency { get; init; }
    public int? VramResidentPercent { get; init; }
    public DateTimeOffset? ResidencyVerifiedAtUtc { get; init; }
}

public sealed record ExperimentTaskTelemetryRecord(
    Guid WorkflowId,
    LocalTaskProfile TaskProfile,
    string Model,
    ModelExecutionOutcome Outcome,
    TimeSpan TotalDuration,
    int ColdExecutions,
    int WarmExecutions,
    bool UsedFallback,
    int InputTokens,
    int OutputTokens,
    int LocalTokensProcessed,
    int EstimatedCloudGenerationTokensSaved,
    int EstimatedNetCloudContextTokensSaved,
    string CatalogVersion,
    DateTimeOffset RecordedAtUtc);

public sealed record ModelTelemetryReadResult(
    IReadOnlyList<ModelTelemetryRecord> Records,
    int Unreadable);

public sealed class ModelTelemetryStore
{
    private static readonly string[] ForbiddenMemberFragments =
        ["prompt", "answer", "content", "image", "path", "secret"];

    public ModelTelemetryStore(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        MetricsDirectory = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "telemetry",
            "metrics");
        ExperimentTasksDirectory = Path.Combine(
            Path.GetFullPath(runtimeRoot),
            "telemetry",
            "experiment-tasks");
    }

    public string MetricsDirectory { get; }

    public string ExperimentTasksDirectory { get; }

    public async Task AppendAsync(
        ModelTelemetryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        EnsureContentFree(record);
        Directory.CreateDirectory(MetricsDirectory);
        if (record.SchemaVersion == 2)
        {
            await TelemetryFile.WriteOnceAsync(Path.Combine(MetricsDirectory, $"{record.AttemptId:N}.json"),
                JsonSerializer.Serialize(record, LocalAiJson.Strict), cancellationToken);
            return;
        }
        var destination = Path.Combine(
            MetricsDirectory,
            $"{record.RecordedAtUtc.UtcTicks:D19}-{record.JobId:N}-{Guid.NewGuid():N}.json");
        var temporary = destination + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    LocalAiJson.Strict,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<IReadOnlyList<ModelTelemetryRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(MetricsDirectory))
        {
            return [];
        }

        var records = new List<ModelTelemetryRecord>();
        foreach (var path in Directory.EnumerateFiles(MetricsDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            records.Add(
                ValidateRecord(await JsonSerializer.DeserializeAsync<ModelTelemetryRecord>(
                    stream,
                    LocalAiJson.Strict,
                    cancellationToken)
                ?? throw new InvalidDataException("Telemetry record is empty.")));
        }

        return records.OrderBy(record => record.RecordedAtUtc).ToArray();
    }

    /// <summary>
    /// Reads what is there, and says how much was not.
    ///
    /// <see cref="ReadAllAsync"/> refuses a directory containing one unreadable file, which is
    /// the right answer for a caller that is about to compute something from every record. It is
    /// the wrong answer for a report over a month of them: a machine that lost power during an
    /// append leaves exactly one truncated file behind, and losing thirty days of measurements to
    /// it teaches whoever hits that to stop running the report.
    ///
    /// The count is returned rather than swallowed. A report that quietly drops records is worse
    /// than one that refuses to run, because nothing about its output looks wrong.
    /// </summary>
    public async Task<ModelTelemetryReadResult> ReadForReportAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(MetricsDirectory))
        {
            return new ModelTelemetryReadResult([], 0);
        }

        var records = new List<ModelTelemetryRecord>();
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(MetricsDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                records.Add(
                    ValidateRecord(await JsonSerializer.DeserializeAsync<ModelTelemetryRecord>(
                        stream,
                        LocalAiJson.Strict,
                        cancellationToken)
                    ?? throw new InvalidDataException("Telemetry record is empty.")));
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException)
            {
                unreadable++;
            }
        }

        return new ModelTelemetryReadResult(records.OrderBy(record => record.RecordedAtUtc).ToArray(), unreadable);
    }

    private static ModelTelemetryRecord ValidateRecord(ModelTelemetryRecord record)
    {
        if (record.SchemaVersion is not (1 or 2)) throw new JsonException("unsupported_attempt_schema");
        if (record.JobId == Guid.Empty || string.IsNullOrWhiteSpace(record.Model)
            || !Enum.IsDefined(record.Outcome) || !Enum.IsDefined(record.TaskProfile))
            throw new JsonException("invalid_attempt_record");
        if (record.SchemaVersion == 2 && (record.AttemptId is null || record.AttemptId == Guid.Empty
            || record.WorkflowId == Guid.Empty || record.InputCharacters < 0 || record.OutputCharacters < 0
            || record.BackendInputTokens < 0 || record.BackendOutputTokens < 0
            || record.CloudTokenSavings is not { Status: UsageValueStatus.Unknown, Value: null }))
            throw new JsonException("invalid_attempt_usage");
        if (record.Residency is { } residency && !Enum.IsDefined(residency)
            || (record.Residency is null) != (record.VramResidentPercent is null)
            || (record.Residency is null) != (record.ResidencyVerifiedAtUtc is null)
            || record.VramResidentPercent is < 0 or > 100)
            throw new JsonException("invalid_attempt_residency");
        return record;
    }

    public async Task AppendExperimentTaskAsync(
        ExperimentTaskTelemetryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureContentFree(record);
        Directory.CreateDirectory(ExperimentTasksDirectory);
        var destination = Path.Combine(
            ExperimentTasksDirectory,
            $"{record.WorkflowId:N}.json");
        if (File.Exists(destination))
        {
            return;
        }

        // Trimming belongs to the operation that changes the history, not to the one that reads
        // it. Doing it here keeps the week-long bound this store has always had while leaving a
        // report free to be run as often as anyone likes.
        PruneExperimentTasks(record.RecordedAtUtc - ExperimentTaskRetention);

        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    LocalAiJson.Strict,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// How long a completed experiment task keeps its measurements. The experiment state keeps
    /// the attempt and outcome counts for as long as the experiment runs; this bounds only the
    /// per-task timings and token figures.
    /// </summary>
    public static TimeSpan ExperimentTaskRetention { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Deletes experiment task records recorded before <paramref name="cutoff"/>.
    ///
    /// Failures are swallowed: this is housekeeping attached to a write, and a file that cannot
    /// be removed right now must not fail the record that triggered the attempt.
    /// </summary>
    public void PruneExperimentTasks(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(ExperimentTasksDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(ExperimentTasksDirectory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ExperimentTaskTelemetryRecord>(
                    File.ReadAllText(path),
                    LocalAiJson.Strict);
                if (record is not null && record.RecordedAtUtc < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or
                    InvalidDataException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the experiment task records, and only reads them.
    ///
    /// This used to delete anything older than seven days on its way through, which made the
    /// experiment report destructive: looking at it discarded the history it was reporting on,
    /// and the more often it was consulted the less there was left to consult. A pair with six
    /// completed attempts answered with one, because the other five files had been deleted by
    /// earlier reads.
    ///
    /// Retention is a policy decision that belongs to a prune, not a side effect of a getter.
    /// </summary>
    public async Task<IReadOnlyList<ExperimentTaskTelemetryRecord>>
        ReadExperimentTasksAsync(
            CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ExperimentTasksDirectory))
        {
            return [];
        }

        var records = new List<ExperimentTaskTelemetryRecord>();
        foreach (var path in Directory
                     .EnumerateFiles(ExperimentTasksDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            ExperimentTaskTelemetryRecord record;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                record = await JsonSerializer
                    .DeserializeAsync<ExperimentTaskTelemetryRecord>(
                        stream,
                        LocalAiJson.Strict,
                        cancellationToken)
                ?? throw new InvalidDataException(
                    "Experiment task telemetry record is empty.");
            }

            records.Add(record);
        }

        return records.AsReadOnly();
    }

    public static void EnsureContentFree(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Inspect(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void Inspect(object? value, HashSet<object> visited)
    {
        if (value is null ||
            value is string ||
            value.GetType().IsPrimitive ||
            value is decimal or Guid or DateTimeOffset or TimeSpan ||
            value.GetType().IsEnum)
        {
            return;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                RejectName(entry.Key?.ToString());
                Inspect(entry.Value, visited);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Inspect(item, visited);
            }

            return;
        }

        foreach (var property in value.GetType().GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            RejectName(property.Name);
            Inspect(property.GetValue(value), visited);
        }
    }

    private static void RejectName(string? name)
    {
        if (name is not null &&
            ForbiddenMemberFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Telemetry member '{name}' is not content-free.");
        }
    }
}
