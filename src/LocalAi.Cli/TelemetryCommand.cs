using LocalAi.Broker;
using LocalAi.Cli.Resources;
using LocalAi.Contracts;
using LocalLm.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LocalAi.Cli;

public sealed record LatencySummary(TimeSpan Median, TimeSpan P90, TimeSpan Longest);

public sealed record TelemetryOutcomeCount(ModelExecutionOutcome Outcome, int Attempts);

public sealed record TelemetryTokenTotal(UsageValue Total, int Known, int Unknown, int NotApplicable);

public sealed record TelemetryBreakdown(
    string Name,
    int Jobs,
    int Attempts,
    int Succeeded,
    int Cold,
    int Fallback,
    TimeSpan MedianExecution,
    long? NetTokensSaved);

public sealed record TelemetrySummary(
    int Jobs,
    int Unreadable,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest,
    IReadOnlyList<TelemetryOutcomeCount> Outcomes,
    int Cold,
    int Fallback,
    LatencySummary Queue,
    LatencySummary Execution,
    long? GrossTokensSaved,
    long? VerificationTokens,
    long? NetTokensSaved,
    IReadOnlyList<TelemetryBreakdown> ByModel,
    IReadOnlyList<TelemetryBreakdown> ByProfile)
{
    public int Attempts { get; init; }
    public int Workflows { get; init; }
    public int Calls { get; init; }
    public int DeliveredResults { get; init; }
    public int UnreportedJobs { get; init; }
    public TelemetryTokenTotal InputSavings { get; init; } = new(UsageValue.Unknown(), 0, 0, 0);
    public TelemetryTokenTotal OutputSavings { get; init; } = new(UsageValue.Unknown(), 0, 0, 0);
    public UsageValue BackendInputTokens { get; init; } = UsageValue.Unknown();
    public UsageValue BackendOutputTokens { get; init; } = UsageValue.Unknown();
}

/// <summary>
/// Reports what the broker has already been measuring.
///
/// Every delegated job leaves a record under the runtime root and they are kept for a month, but
/// until now the only thing that could read any of them was the experiment report, which covers
/// the handful of jobs belonging to a running model experiment. The per-job records — every
/// routing decision, every cold load, every fallback, every estimate of what a job saved — were
/// written, retained, pruned on schedule, and never once read. Measuring something and never
/// looking at it is worse than not measuring it: it costs disk and buys the impression of
/// evidence.
///
/// This is a separate command rather than a part of <see cref="DoctorCommand"/> on purpose. The
/// doctor answers whether an installation is sound and says so in its exit code; this answers
/// what the installation has been doing, and there is no state of the world in which the answer
/// is a failure. Folding a report with no failure mode into a command whose exit code means
/// "something is broken" would either make the code meaningless or make a quiet month look like
/// a fault.
///
/// Attempts are diagnostics. Cloud comparisons belong to completed surface calls, which may
/// link several jobs and attempts. Legacy scalar estimates are never added to those comparisons.
/// </summary>
public static class TelemetryCommand
{
    internal static async Task<int> ExecuteAsync(string[] arguments, string runtimeRoot, CommandStreams streams,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Length == 0) return await ExecuteAsync(runtimeRoot, streams.Out, cancellationToken);
        if (arguments is not ["--call-id", var value] || !Guid.TryParse(value, out var id) || id == Guid.Empty)
        {
            UsageCall.Current?.Refused("invalid_call_id");
            streams.Error.WriteLine(CliText.UsageReportArguments);
            return 2;
        }
        var report = await new UsageReportStore(runtimeRoot).ReadAsync(id, cancellationToken);
        if (report is null)
        {
            UsageCall.Current?.Refused("usage_report_not_found");
            streams.Error.WriteLine(CliText.UsageReportNotFound(id));
            return 1;
        }
        streams.Out.WriteLine(UsageReportJson.Write(report));
        return 0;
    }

    public static async Task<int> ExecuteAsync(
        string runtimeRoot,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentNullException.ThrowIfNull(output);
        var read = await new ModelTelemetryStore(runtimeRoot)
            .ReadForReportAsync(cancellationToken);
        var usage = await new UsageReportStore(runtimeRoot).ReadForReportAsync(cancellationToken);
        output.Write(Render(Summarize(read with { Unreadable = read.Unreadable + usage.Unreadable }, usage.Reports), Path.GetFullPath(runtimeRoot)));
        return 0;
    }

    public static TelemetrySummary Summarize(ModelTelemetryReadResult read, IReadOnlyList<UsageReport>? reports = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        var unreadable = read.Unreadable;
        var records = Unique(read.Records, record => record.AttemptId,
            record => JsonSerializer.Serialize(record, LocalAiJson.Strict), ref unreadable);
        var calls = Unique(reports ?? [], report => report.Identity.CallId, UsageReportJson.Write, ref unreadable);
        var reportedJobs = calls.SelectMany(report => report.Identity.JobIds).ToHashSet();
        var jobs = records.Select(record => record.JobId).Concat(reportedJobs).Distinct().ToArray();
        return new TelemetrySummary(
            jobs.Length,
            unreadable,
            records.Length == 0 ? null : records.Min(record => record.RecordedAtUtc),
            records.Length == 0 ? null : records.Max(record => record.RecordedAtUtc),
            records
                .GroupBy(record => record.Outcome)
                .Select(group => new TelemetryOutcomeCount(group.Key, group.Count()))
                .OrderByDescending(outcome => outcome.Attempts)
                .ThenBy(outcome => outcome.Outcome.ToString(), StringComparer.Ordinal)
                .ToArray(),
            records.Count(record => record.WasCold),
            records.Count(record => record.UsedFallback),
            Latency(records.Select(record => record.QueueDuration)),
            Latency(records.Select(record => record.ExecutionDuration)),
            null,
            null,
            null,
            Breakdown(records, record => record.Model),
            Breakdown(records, record => record.TaskProfile.ToString()))
        {
            Attempts = records.Length,
            Workflows = records.Select(record => record.WorkflowId).Concat(calls.Select(report => report.Identity.WorkflowId))
                .Where(id => id is not null).Distinct().Count(),
            Calls = calls.Length,
            DeliveredResults = calls.Count(report => report.Outcome.Delivery == UsageDelivery.Delivered),
            UnreportedJobs = jobs.Count(job => !reportedJobs.Contains(job)),
            InputSavings = Savings(calls, UsageCloudTokens.InputSaved),
            OutputSavings = Savings(calls, UsageCloudTokens.OutputSaved),
            BackendInputTokens = AttemptTokens(records.Select(record => record.BackendInputTokens)),
            BackendOutputTokens = AttemptTokens(records.Select(record => record.BackendOutputTokens)),
        };
    }

    private static T[] Unique<T>(IEnumerable<T> records, Func<T, Guid?> key, Func<T, string> serialize, ref int unreadable)
    {
        var list = new List<T>();
        foreach (var group in records.GroupBy(key))
        {
            if (group.Key is null) { list.AddRange(group); continue; }
            var first = group.First();
            if (group.Count() == 1) { list.Add(first); continue; }
            using var expected = JsonDocument.Parse(serialize(first));
            var conflict = group.Skip(1).Any(record =>
            {
                using var other = JsonDocument.Parse(serialize(record));
                return !JsonElement.DeepEquals(expected.RootElement, other.RootElement);
            });
            if (conflict) unreadable += group.Count();
            else list.Add(first);
        }
        return list.ToArray();
    }

    private static UsageValue AttemptTokens(IEnumerable<long?> values)
    {
        var counts = values.ToArray();
        if (counts.Length == 0 || counts.Any(value => value is null or < 0))
            return UsageValue.Unknown("attempt_usage_incomplete");
        try { return UsageValue.Measured(counts.Aggregate(0L, (sum, value) => checked(sum + value!.Value))); }
        catch (OverflowException) { return UsageValue.Unknown("token_count_overflow"); }
    }

    private static TelemetryTokenTotal Savings(IReadOnlyList<UsageReport> reports, UsageCloudTokens key)
    {
        var applicable = reports.Where(report => report.ComparisonKind != UsageComparisonKind.NotApplicable).ToArray();
        var eligible = applicable.Where(report => report.Outcome.Technical == UsageTechnicalOutcome.Succeeded
                && report.Outcome.Validation is UsageValidation.Passed or UsageValidation.NotApplicable
                && report.Outcome.Delivery == UsageDelivery.Delivered
                && report.CloudTokens[key].Status is UsageValueStatus.Measured or UsageValueStatus.Estimated).ToArray();
        var sharedJobs = eligible.SelectMany(report => report.Identity.JobIds).GroupBy(id => id)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var sharedAttempts = eligible.SelectMany(report => report.Identity.AttemptIds).GroupBy(id => id)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        // Two callers can receive the same cached execution. Without an independent comparison
        // for its reuse, crediting each linked report would duplicate the replaced generation.
        var known = eligible.Where(report => !report.Identity.JobIds.Any(sharedJobs.Contains)
            && !report.Identity.AttemptIds.Any(sharedAttempts.Contains)).ToArray();
        var unknown = applicable.Length - known.Length;
        var notApplicable = reports.Count - applicable.Length;
        UsageValue total;
        if (known.Length == 0)
            total = reports.Count > 0 && notApplicable == reports.Count
                ? UsageValue.NotApplicable("administrative_operation")
                : UsageValue.Unknown(eligible.Length > 0 ? "shared_execution" : "comparison_not_recorded");
        else if (known.Select(report => (report.MethodologyVersion, report.Comparison)).Distinct().Count() != 1)
            total = UsageValue.Unknown("incompatible_comparisons");
        else
        {
            try
            {
                var sum = known.Aggregate(0L, (value, report) => checked(value + report.CloudTokens[key].Value!.Value));
                total = known.All(report => report.CloudTokens[key].Status == UsageValueStatus.Measured)
                    ? UsageValue.Measured(sum) : UsageValue.Estimated(sum, "call-comparison-sum-v1");
            }
            catch (OverflowException) { total = UsageValue.Unknown("token_count_overflow"); }
        }
        return new(total, known.Length, unknown, notApplicable);
    }

    public static string Render(TelemetrySummary summary, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var text = new StringBuilder();
        if (summary.Attempts == 0 && summary.Calls == 0)
        {
            // Not a failure and not an error: a runtime that has never been asked to run anything
            // locally has nothing to report, and so does one whose records have all aged out.
            text.AppendLine(summary.Unreadable > 0
                ? CliText.TelemetryNoneUnreadable(runtimeRoot)
                : CliText.TelemetryNone(runtimeRoot));
            return text.ToString();
        }

        if (summary.Attempts > 0) text.AppendLine(CliText.TelemetryRecorded(
            summary.Attempts,
            Moment(summary.Earliest),
            Moment(summary.Latest)));
        AppendUnreadable(text, summary);
        text.AppendLine(CliText.Catalogue.Format("TelemetryJobCounts", summary.Jobs, summary.Workflows));
        text.AppendLine(CliText.Catalogue.Format("TelemetryResultCounts", summary.Calls, summary.DeliveredResults));
        if (summary.UnreportedJobs > 0)
            text.AppendLine(CliText.Catalogue.Format("TelemetryUnreportedJobs", summary.UnreportedJobs));
        AppendSavings(text, "TelemetryCloudInputSavings", summary.InputSavings);
        AppendSavings(text, "TelemetryCloudOutputSavings", summary.OutputSavings);
        text.AppendLine(CliText.Catalogue.Get("TelemetrySavingsSign"));
        if (summary.Attempts == 0) return text.ToString();
        text.AppendLine();
        text.AppendLine(CliText.Catalogue.Get("TelemetryAttemptDiagnostics"));
        text.AppendLine(
            "outcome     " + string.Join(
                ", ",
                summary.Outcomes.Select(outcome =>
                    $"{outcome.Outcome} {outcome.Attempts} ({Percent(outcome.Attempts, summary.Attempts)})")));
        text.AppendLine(
            $"loading     {summary.Cold} cold ({Percent(summary.Cold, summary.Attempts)}), " +
            $"{summary.Attempts - summary.Cold} warm " +
            $"({Percent(summary.Attempts - summary.Cold, summary.Attempts)})");
        text.AppendLine(
            $"fallback    {summary.Fallback} " +
            $"({Percent(summary.Fallback, summary.Attempts)})");
        foreach (var line in FailureOutliers(summary.ByModel))
        {
            text.AppendLine(line);
        }
        text.AppendLine($"queue       {Latencies(summary.Queue)}");
        text.AppendLine($"execution   {Latencies(summary.Execution)}");

        AppendBreakdown(text, CliText.TelemetryByModel, summary.ByModel);
        AppendBreakdown(text, CliText.TelemetryByProfile, summary.ByProfile);
        return text.ToString();
    }

    private static void AppendSavings(StringBuilder text, string key, TelemetryTokenTotal total)
    {
        var formatted = total.Total.Status switch
        {
            UsageValueStatus.Measured => total.Total.Value!.Value.ToString(CultureInfo.InvariantCulture),
            UsageValueStatus.Estimated => TokenEstimator.DescribeRange(total.Total.Value!.Value),
            UsageValueStatus.NotApplicable => CliText.Catalogue.Get("TelemetrySavingsNotApplicable"),
            _ => $"{CliText.Catalogue.Get("TelemetrySavingsUnknown")} ({total.Total.Reason})",
        };
        text.AppendLine(CliText.Catalogue.Format(key, formatted));
        text.AppendLine(CliText.Catalogue.Format("TelemetrySavingsCoverage", total.Known, total.Unknown, total.NotApplicable));
    }

    /// <summary>
    /// One plain line for a model that owns the failures. The pattern this surfaces stayed
    /// invisible for a month on the reference machine: one model failed half of its jobs —
    /// every technical failure in the system — while the profile view showed acceptable
    /// success rates, because the fallback quietly absorbed each miss. The numbers were all
    /// printed; seeing the pattern required cross-reading two tables. A model is named here
    /// when it has enough jobs to mean something, fails at least a quarter of them, and
    /// carries at least half of all recorded failures.
    /// </summary>
    public static IReadOnlyList<string> FailureOutliers(
        IReadOnlyList<TelemetryBreakdown> byModel)
    {
        ArgumentNullException.ThrowIfNull(byModel);
        var totalFailures = byModel.Sum(row => row.Attempts - row.Succeeded);
        if (totalFailures == 0)
        {
            return [];
        }

        return byModel
            .Where(row => row.Attempts >= 10)
            .Select(row => (Row: row, Failures: row.Attempts - row.Succeeded))
            .Where(entry =>
                entry.Failures * 4 >= entry.Row.Attempts &&
                entry.Failures * 2 >= totalFailures)
            .OrderByDescending(entry => entry.Failures)
            .Select(entry =>
                "attention   " + CliText.TelemetryFailureOutlier(
                    entry.Row.Name,
                    Percent(entry.Failures, entry.Row.Attempts),
                    entry.Failures,
                    totalFailures))
            .ToArray();
    }

    private static void AppendUnreadable(StringBuilder text, TelemetrySummary summary)
    {
        if (summary.Unreadable > 0)
        {
            text.AppendLine(
                CliText.TelemetryUnreadable(summary.Unreadable));
        }
    }

    private static void AppendBreakdown(
        StringBuilder text,
        string title,
        IReadOnlyList<TelemetryBreakdown> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine(title);
        var width = rows.Max(row => row.Name.Length);
        foreach (var row in rows)
        {
            text.AppendLine(
                $"  {row.Name.PadRight(width)}  {CliText.Catalogue.Format("TelemetryAttemptCount", row.Attempts)}  " +
                $"{Percent(row.Succeeded, row.Attempts),4} ok  " +
                $"{Percent(row.Cold, row.Attempts),4} cold  " +
                $"{Percent(row.Fallback, row.Attempts),4} fallback  " +
                $"median {Duration(row.MedianExecution),7}");
        }
    }

    private static IReadOnlyList<TelemetryBreakdown> Breakdown(
        IReadOnlyList<ModelTelemetryRecord> records,
        Func<ModelTelemetryRecord, string> key) =>
        records
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => new TelemetryBreakdown(
                group.Key,
                group.Select(record => record.JobId).Distinct().Count(),
                group.Count(),
                group.Count(record => record.Outcome == ModelExecutionOutcome.Success),
                group.Count(record => record.WasCold),
                group.Count(record => record.UsedFallback),
                Latency(group.Select(record => record.ExecutionDuration)).Median,
                null))
            .OrderByDescending(row => row.Attempts)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();

    private static LatencySummary Latency(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0
            ? new LatencySummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
            : new LatencySummary(
                Percentile(sorted, 0.5),
                Percentile(sorted, 0.9),
                sorted[^1]);
    }

    /// <summary>
    /// Nearest-rank, so every figure printed is a duration some job actually took. An
    /// interpolated median is a number that never happened, which is a strange thing to hand
    /// someone who is about to go looking for the job that took it.
    /// </summary>
    private static TimeSpan Percentile(TimeSpan[] sorted, double percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static string Latencies(LatencySummary latency) =>
        $"median {Duration(latency.Median)}, p90 {Duration(latency.P90)}, " +
        $"longest {Duration(latency.Longest)}";

    internal static string Duration(TimeSpan value) => value.TotalMilliseconds switch
    {
        < 1000 => $"{value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms",
        < 60_000 => $"{value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s",
        _ => $"{(int)value.TotalMinutes}m{value.Seconds:D2}s",
    };

    private static string Percent(int part, int whole) =>
        whole == 0
            ? "0%"
            : $"{(100.0 * part / whole).ToString("F0", CultureInfo.InvariantCulture)}%";

    private static string Moment(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
}
