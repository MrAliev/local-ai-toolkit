using LocalAi.Broker;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// A report is only worth having if the numbers in it move when the thing they describe moves,
/// so these write records that differ in exactly one respect and assert that the summary says so.
/// </summary>
public sealed class TelemetryCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localai-telemetry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_failed_attempt_and_successful_fallback_are_one_job()
    {
        var first = Record() with { Outcome = ModelExecutionOutcome.TechnicalFailure };
        await Write(first);
        await Write(Record() with { JobId = first.JobId, UsedFallback = true });

        var summary = await Summarize();

        Assert.Equal(1, summary.Jobs);
    }

    [Fact]
    public async Task Legacy_estimates_without_delivery_or_comparison_evidence_are_unknown()
    {
        await Write(Record());

        var summary = await Summarize();

        Assert.DoesNotContain("~900", TelemetryCommand.Render(summary, _root), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_final_call_report_is_read_even_without_attempt_files()
    {
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", "ask_local", Guid.NewGuid());
        report = report with { Outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.Passed, UsageDelivery.Delivered, null, null, null) };
        report = CloudTokenMethodology.Apply(report, new("explicit-reading-v1",
            [new CloudTextSource("snapshot", new string('a', 400), Presence: CloudContextPresence.NotPresent)],
            "answer", "", ""), "answer", HeuristicCloudTokenProfile.Instance);
        var directory = Path.Combine(_root, "telemetry", "usage");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{report.Identity.CallId:N}.json"),
            UsageReportJson.Write(report), TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        await TelemetryCommand.ExecuteAsync(_root, output, TestContext.Current.CancellationToken);
        Assert.Contains("Calls with usage reports: 1; results confirmed delivered: 1.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("~98", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The pattern this line surfaces stayed invisible for a month: one model failed half of
    /// its jobs — every technical failure in the system — while the fallback quietly absorbed
    /// each miss and the profile view showed acceptable success rates. All the numbers were
    /// printed; seeing them required cross-reading two tables.
    /// </summary>
    [Fact]
    public async Task A_model_that_owns_the_failures_is_named_in_one_line()
    {
        for (var i = 0; i < 5; i++)
        {
            await Write(Record() with
            {
                Model = "sick-model",
                Outcome = ModelExecutionOutcome.TechnicalFailure,
            });
        }

        for (var i = 0; i < 5; i++)
        {
            await Write(Record() with { Model = "sick-model" });
        }

        for (var i = 0; i < 10; i++)
        {
            await Write(Record() with { Model = "healthy-model" });
        }

        var rendered = TelemetryCommand.Render(await Summarize(), _root);

        Assert.Contains(
            "attention   sick-model: 50% of attempts were unsuccessful (5 of 5 unsuccessful attempts overall).",
            rendered,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "attention   healthy-model",
            rendered,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Failures spread across the catalogue are background noise, not one sick model: each of
    /// these fails a fifth of its jobs, below the quarter the line requires, and neither owns
    /// the failure count alone.
    /// </summary>
    [Fact]
    public async Task Scattered_failures_name_no_model()
    {
        foreach (var model in new[] { "first-model", "second-model" })
        {
            for (var i = 0; i < 2; i++)
            {
                await Write(Record() with
                {
                    Model = model,
                    Outcome = ModelExecutionOutcome.TechnicalFailure,
                });
            }

            for (var i = 0; i < 8; i++)
            {
                await Write(Record() with { Model = model });
            }
        }

        var rendered = TelemetryCommand.Render(await Summarize(), _root);

        Assert.DoesNotContain("attention   ", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ten jobs is the floor: two failures out of three would satisfy every ratio, and a
    /// sample that small names a model on what may be one bad afternoon.
    /// </summary>
    [Fact]
    public async Task Too_few_jobs_never_name_a_model()
    {
        await Write(Record() with
        {
            Model = "tiny-sample",
            Outcome = ModelExecutionOutcome.TechnicalFailure,
        });
        await Write(Record() with
        {
            Model = "tiny-sample",
            Outcome = ModelExecutionOutcome.TechnicalFailure,
        });
        await Write(Record() with { Model = "tiny-sample" });

        var rendered = TelemetryCommand.Render(await Summarize(), _root);

        Assert.DoesNotContain("attention   ", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outcomes_are_counted_separately_rather_than_summed()
    {
        await Write(Record() with { Outcome = ModelExecutionOutcome.Success });
        await Write(Record() with { Outcome = ModelExecutionOutcome.Success });
        await Write(Record() with { Outcome = ModelExecutionOutcome.StructuralFailure });

        var summary = await Summarize();

        Assert.Equal(3, summary.Jobs);
        Assert.Equal(
            2,
            summary.Outcomes
                .Single(outcome => outcome.Outcome == ModelExecutionOutcome.Success)
                .Attempts);
        Assert.Equal(
            1,
            summary.Outcomes
                .Single(outcome => outcome.Outcome == ModelExecutionOutcome.StructuralFailure)
                .Attempts);
    }

    /// <summary>
    /// The two totals are deliberately different, and one record is both. Equal totals would let
    /// an implementation that counts one condition twice pass this, and a sample where nothing
    /// overlaps would let one that treats them as exclusive pass it too.
    /// </summary>
    [Fact]
    public async Task Cold_and_fallback_are_counted_independently_of_each_other()
    {
        await Write(Record() with { WasCold = true, UsedFallback = false });
        await Write(Record() with { WasCold = true, UsedFallback = true });
        await Write(Record() with { WasCold = true, UsedFallback = false });
        await Write(Record() with { WasCold = false, UsedFallback = true });
        await Write(Record() with { WasCold = false, UsedFallback = false });

        var summary = await Summarize();

        Assert.Equal(3, summary.Cold);
        Assert.Equal(2, summary.Fallback);
    }

    /// <summary>
    /// The percentile has to be the one the sample actually contains. An implementation that
    /// averages, or that takes the last value, passes an all-equal sample and fails here.
    /// </summary>
    [Fact]
    public async Task Latencies_report_a_median_and_a_p90_from_the_sample()
    {
        foreach (var seconds in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 100 })
        {
            await Write(Record() with
            {
                ExecutionDuration = TimeSpan.FromSeconds(seconds),
                QueueDuration = TimeSpan.FromMilliseconds(seconds),
            });
        }

        var summary = await Summarize();

        Assert.Equal(TimeSpan.FromSeconds(5), summary.Execution.Median);
        Assert.Equal(TimeSpan.FromSeconds(9), summary.Execution.P90);
        Assert.Equal(TimeSpan.FromSeconds(100), summary.Execution.Longest);
        Assert.Equal(TimeSpan.FromMilliseconds(5), summary.Queue.Median);
    }

    [Fact]
    public async Task Records_are_grouped_by_model_and_by_task_profile()
    {
        await Write(Record() with
        {
            Model = "qwen3-vl:8b-instruct-q8_0",
            TaskProfile = LocalTaskProfile.VisualAnalysis,
        });
        await Write(Record() with
        {
            Model = "qwen3-vl:8b-instruct-q8_0",
            TaskProfile = LocalTaskProfile.LogTriage,
        });
        await Write(Record() with
        {
            Model = "qwen3-embedding:8b-q8_0",
            TaskProfile = LocalTaskProfile.LogTriage,
        });

        var summary = await Summarize();

        Assert.Equal(
            2,
            summary.ByModel.Single(row => row.Name == "qwen3-vl:8b-instruct-q8_0").Jobs);
        Assert.Equal(
            1,
            summary.ByModel.Single(row => row.Name == "qwen3-embedding:8b-q8_0").Jobs);
        Assert.Equal(
            2,
            summary.ByProfile.Single(row => row.Name == nameof(LocalTaskProfile.LogTriage)).Jobs);
    }

    /// <summary>
    /// Adding heuristic estimates does not calibrate them. The total remains explicitly
    /// approximate and must not acquire an unsupported percentage interval.
    /// </summary>
    [Fact]
    public void Token_savings_are_printed_as_a_point_estimate_without_an_invented_band()
    {
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", "ask_local", Guid.NewGuid());
        report = report with { Outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.Passed, UsageDelivery.Delivered, null, null, null) };
        report = CloudTokenMethodology.Apply(report, new("explicit-reading-v1",
            [new CloudTextSource("snapshot", new string('a', 400_000), Presence: CloudContextPresence.NotPresent)],
            "", "", ""), "", HeuristicCloudTokenProfile.Instance);
        var text = TelemetryCommand.Render(TelemetryCommand.Summarize(new([], 0), [report]), _root);

        Assert.Contains("~100K", text, StringComparison.Ordinal);
        Assert.DoesNotContain("–", text, StringComparison.Ordinal);
        Assert.DoesNotContain("100000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("100,000", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine that loses power during an append leaves one truncated file behind. Losing a
    /// month of measurements to it would teach whoever hit that to stop running the report.
    /// </summary>
    [Fact]
    public async Task One_unreadable_file_is_counted_rather_than_losing_the_report()
    {
        await Write(Record());
        await Write(Record());
        await File.WriteAllTextAsync(
            Path.Combine(_root, "telemetry", "metrics", "99999999999999999999-broken.json"),
            "{\"JobId\": ",
            TestContext.Current.CancellationToken);

        var summary = await Summarize();

        Assert.Equal(2, summary.Jobs);
        Assert.Equal(1, summary.Unreadable);
        Assert.Contains(
            "unreadable or conflicting",
            TelemetryCommand.Render(summary, _root),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_has_delegated_nothing_says_so_instead_of_printing_zeroes()
    {
        Directory.CreateDirectory(_root);

        var text = TelemetryCommand.Render(await Summarize(), _root);

        Assert.Contains("No job telemetry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("median", text, StringComparison.Ordinal);
    }

    private async Task<TelemetrySummary> Summarize() =>
        TelemetryCommand.Summarize(
            await new ModelTelemetryStore(_root).ReadForReportAsync(
                TestContext.Current.CancellationToken));

    private Task Write(ModelTelemetryRecord record) =>
        new ModelTelemetryStore(_root).AppendAsync(
            record,
            TestContext.Current.CancellationToken);

    private static ModelTelemetryRecord Record() => new(
        Guid.NewGuid(),
        LocalTaskProfile.LogTriage,
        "qwen3-vl:8b-instruct-q8_0",
        ContextTokens: 8192,
        LocalSizeBucket.Small,
        LocalSizeBucket.Small,
        WasCold: false,
        ModelSwitched: false,
        UsedFallback: false,
        "schema:pass",
        ModelExecutionOutcome.Success,
        QueueDuration: TimeSpan.FromMilliseconds(20),
        LoadDuration: TimeSpan.Zero,
        ExecutionDuration: TimeSpan.FromSeconds(2),
        TotalDuration: TimeSpan.FromSeconds(2),
        EstimatedGrossCloudTokensSaved: 1_000,
        EstimatedVerificationTokens: 100,
        EstimatedNetCloudTokensSaved: 900,
        "test",
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
