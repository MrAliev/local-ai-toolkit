using LocalAi.Broker;
using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class TelemetryAggregationTests
{
    [Fact]
    public void Retry_keeps_all_attempt_usage_but_counts_a_delivered_result_once()
    {
        var job = Guid.NewGuid();
        var failed = Attempt(job, ModelExecutionOutcome.TechnicalFailure, 120, 3);
        var succeeded = Attempt(job, ModelExecutionOutcome.Success, 100, 20);
        var report = Delivered([job], [failed.AttemptId!.Value, succeeded.AttemptId!.Value]);
        var summary = TelemetryCommand.Summarize(new([failed, succeeded], 0), [report]);

        Assert.Equal(1, summary.Jobs);
        Assert.Equal(2, summary.Attempts);
        Assert.Equal(1, summary.DeliveredResults);
        Assert.Equal(220, summary.BackendInputTokens.Value);
        Assert.Equal(23, summary.BackendOutputTokens.Value);
        Assert.Equal(98, summary.InputSavings.Total.Value);
        Assert.Equal(20, summary.OutputSavings.Total.Value);
    }

    [Fact]
    public void Multiple_jobs_in_one_workflow_are_one_call_and_one_comparison()
    {
        var workflow = Guid.NewGuid();
        var first = Attempt(Guid.NewGuid(), ModelExecutionOutcome.Success, 40, 4) with { WorkflowId = workflow };
        var second = Attempt(Guid.NewGuid(), ModelExecutionOutcome.Success, 50, 5) with { WorkflowId = workflow };
        var report = Delivered([first.JobId, second.JobId], [first.AttemptId!.Value, second.AttemptId!.Value]);
        report = report with { Identity = report.Identity with { WorkflowId = workflow } };
        var summary = TelemetryCommand.Summarize(new([first, second], 0), [report]);

        Assert.Equal(2, summary.Jobs);
        Assert.Equal(2, summary.Attempts);
        Assert.Equal(1, summary.Workflows);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(1, summary.DeliveredResults);
        Assert.Equal(0, summary.UnreportedJobs);
        Assert.Equal(98, summary.InputSavings.Total.Value);
    }

    [Fact]
    public void Repeated_reads_do_not_double_count_the_same_attempt_or_call()
    {
        var attempt = Attempt(Guid.NewGuid(), ModelExecutionOutcome.Success, 10, 2);
        var report = Delivered([attempt.JobId], [attempt.AttemptId!.Value]);
        var summary = TelemetryCommand.Summarize(new([attempt, attempt], 0), [report, UsageReportJson.Read(UsageReportJson.Write(report))]);

        Assert.Equal(1, summary.Attempts);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(1, summary.DeliveredResults);
        Assert.Equal(10, summary.BackendInputTokens.Value);
        Assert.Equal(98, summary.InputSavings.Total.Value);
    }

    [Fact]
    public void All_attempts_failed_does_not_prove_delivery_or_savings()
    {
        var first = Attempt(Guid.NewGuid(), ModelExecutionOutcome.TechnicalFailure, null, null);
        var second = Attempt(first.JobId, ModelExecutionOutcome.TechnicalFailure, null, null);
        var summary = TelemetryCommand.Summarize(new([first, second], 0));

        Assert.Equal(1, summary.Jobs);
        Assert.Equal(2, summary.Attempts);
        Assert.Equal(0, summary.DeliveredResults);
        Assert.Equal(1, summary.UnreportedJobs);
        Assert.Equal(UsageValueStatus.Unknown, summary.InputSavings.Total.Status);
        Assert.Equal(UsageValueStatus.Unknown, summary.BackendInputTokens.Status);
    }

    [Fact]
    public void Unknown_attempt_usage_is_not_replaced_by_zero()
    {
        var first = Attempt(Guid.NewGuid(), ModelExecutionOutcome.TechnicalFailure, null, null);
        var second = Attempt(first.JobId, ModelExecutionOutcome.Success, 10, 2);
        var summary = TelemetryCommand.Summarize(new([first, second], 0));
        Assert.Equal(UsageValueStatus.Unknown, summary.BackendInputTokens.Status);
        Assert.Null(summary.BackendInputTokens.Value);
    }

    [Fact]
    public void Savings_keep_negative_values_and_independent_unknown_coverage()
    {
        var negative = Delivered([], [], source: "", payload: new string('a', 400));
        var unknown = Delivered([], []);
        unknown = unknown with { CloudTokens = unknown.CloudTokens.ToDictionary(p => p.Key, _ => UsageValue.Unknown("comparison_not_recorded")) };
        var admin = UsageReportingRegistry.CreateReport(UsageSurface.Cli, "localai", "telemetry", Guid.NewGuid());
        var summary = TelemetryCommand.Summarize(new([], 0), [negative, unknown, admin]);
        Assert.Equal(-100, summary.InputSavings.Total.Value);
        Assert.Equal(1, summary.InputSavings.Known);
        Assert.Equal(1, summary.InputSavings.Unknown);
        Assert.Equal(1, summary.InputSavings.NotApplicable);
        Assert.Equal(20, summary.OutputSavings.Total.Value);
    }

    [Fact]
    public void Different_tokenizer_profiles_cannot_be_summed_into_one_figure()
    {
        var first = Delivered([], []);
        var second = Delivered([], []);
        second = second with { Comparison = second.Comparison with { TokenizerProfile = "another-tokenizer" } };
        var summary = TelemetryCommand.Summarize(new([], 0), [first, second]);
        Assert.Equal(UsageValueStatus.Unknown, summary.InputSavings.Total.Status);
        Assert.Equal("incompatible_comparisons", summary.InputSavings.Total.Reason);
    }

    [Fact]
    public void Failed_delivery_cannot_earn_positive_savings_even_from_an_inconsistent_report()
    {
        var report = Delivered([], []);
        report = report with { Outcome = report.Outcome with { Delivery = UsageDelivery.Failed, DeliveryReason = "write_failed" } };
        var summary = TelemetryCommand.Summarize(new([], 0), [report]);
        Assert.Equal(UsageValueStatus.Unknown, summary.InputSavings.Total.Status);
        Assert.Equal(0, summary.DeliveredResults);
    }

    [Fact]
    public void Conflicting_duplicate_call_records_are_counted_unreadable_not_selected_arbitrarily()
    {
        var report = Delivered([], []);
        var changed = report with { Outcome = report.Outcome with { Delivery = UsageDelivery.Failed, DeliveryReason = "write_failed" } };
        var summary = TelemetryCommand.Summarize(new([], 0), [report, changed]);
        Assert.Equal(2, summary.Unreadable);
        Assert.Equal(0, summary.DeliveredResults);
        Assert.Equal(UsageValueStatus.Unknown, summary.InputSavings.Total.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Separate_cached_calls_cannot_credit_the_same_execution_twice(bool shareJob)
    {
        var job = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var first = Delivered([job], [attempt]);
        var cached = Delivered([shareJob ? job : Guid.NewGuid()], [shareJob ? Guid.NewGuid() : attempt]);
        cached = cached with { Models = cached.Models with { Cache = UsageCache.Hit } };
        var summary = TelemetryCommand.Summarize(new([], 0), [first, cached]);
        Assert.Equal(2, summary.Calls);
        Assert.Equal(UsageValueStatus.Unknown, summary.InputSavings.Total.Status);
        Assert.Equal("shared_execution", summary.InputSavings.Total.Reason);
        Assert.Equal(2, summary.InputSavings.Unknown);
        Assert.Equal(UsageValueStatus.Unknown, summary.OutputSavings.Total.Status);
    }

    private static ModelTelemetryRecord Attempt(Guid job, ModelExecutionOutcome outcome, long? input, long? output) =>
        new(job, LocalTaskProfile.ShortSummary, "qwen3.5:9b", 2048, LocalSizeBucket.Small,
            LocalSizeBucket.Small, false, false, false, "none", outcome, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1000, 0, 1000, "test", DateTimeOffset.UtcNow)
        { SchemaVersion = 2, AttemptId = Guid.NewGuid(), BackendInputTokens = input, BackendOutputTokens = output };

    private static UsageReport Delivered(IReadOnlyList<Guid> jobs, IReadOnlyList<Guid> attempts,
        string? source = null, string payload = "answer")
    {
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", "ask_local", Guid.NewGuid());
        report = report with
        {
            Identity = report.Identity with { JobIds = jobs, AttemptIds = attempts },
            Outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.Passed, UsageDelivery.Delivered, null, null, null)
        };
        return CloudTokenMethodology.Apply(report, new("explicit-reading-v1",
            [new CloudTextSource("snapshot", source ?? new string('a', 400), Presence: CloudContextPresence.NotPresent)],
            new string('a', 80), "", ""), payload, HeuristicCloudTokenProfile.Instance);
    }
}
