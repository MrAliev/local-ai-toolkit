using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class RuntimeRequestContextTests
{
    [Fact]
    public void First_read_history_does_not_declare_the_entire_file_necessary()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.SourceText(new string('x', 100_000), "file-read-for-a-small-task");
        Assert.Equal(UsageValue.Unknown("request_context_not_declared"),
            Calculate(call).CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
    }

    [Theory]
    [InlineData(UsageSurface.Mcp, "codesearch", "search_code")]
    [InlineData(UsageSurface.Mcp, "codesearch", "get_code_chunk")]
    [InlineData(UsageSurface.Mcp, "locallm", "search_documents")]
    [InlineData(UsageSurface.Cli, "codesearch", "search")]
    [InlineData(UsageSurface.Cli, "codesearch", "get-chunk")]
    [InlineData(UsageSurface.Cli, "localai", "documents search")]
    public void Retrieval_does_not_credit_whole_files_as_necessary_context(UsageSurface surface, string component, string operation)
    {
        using var call = new UsageCall(surface, component, operation);
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceText(new string('x', 100_000), "large-file");
        var report = Calculate(call);
        Assert.Equal(UsageValue.Unknown("retrieval_baseline_not_established"), report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
        Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
        Assert.Equal(UsageValueStatus.Measured, report.CloudTokens[UsageCloudTokens.InputWithDelegation].Status);
    }

    [Fact]
    public void Omitted_source_history_does_not_assume_first_reading()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.SourceText("The task input.", "task-input");
        var report = Calculate(call);
        Assert.Equal(UsageValue.Unknown("source_context_unknown"), report.CloudTokens[UsageCloudTokens.InputSaved]);
    }

    [Fact]
    public void Local_generated_output_is_not_credit_for_assumed_cloud_generation()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.SourceContext(CloudContextPresence.AlreadyPresent);
        call.Comparison.SourceText("Previously read input.", "task-input");
        call.Comparison.GeneratedText(new string('x', 100_000));
        var report = Calculate(call);
        Assert.Equal(UsageValue.Measured(0), report.CloudTokens[UsageCloudTokens.OutputWithoutDelegation]);
        Assert.True(report.CloudTokens[UsageCloudTokens.OutputSaved].Value <= 0);
        Assert.StartsWith("runtime-request-context-v2:", report.Comparison.ScenarioId);
    }

    [Fact]
    public void Distinct_unlocated_slices_cannot_double_count_one_source()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.SourceText("alpha beta", "same-source");
        call.Comparison.SourceText("beta gamma", "same-source");
        var report = Calculate(call);
        Assert.Equal(UsageValueStatus.Unknown, report.CloudTokens[UsageCloudTokens.InputSaved].Status);
    }

    private static UsageReport Calculate(UsageCall call) => CloudTokenMethodology.Apply(
        call.Complete(), call.Comparison.Scenario("request"), "response", call.Comparison.Profile);

    [Fact]
    public void Retrieval_notice_reports_observed_exchange_without_inventing_savings_or_overrun()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "codesearch", "search_code");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.SourceText("small selected hit", "file");
        var payload = UsagePayload.Finalize(call.Complete(), UsageNotice.Render, text => text,
            call.Comparison.Scenario("request"), call.Comparison.Profile);
        Assert.Contains("necessary source context is not established", payload.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("additional cloud tokens", payload.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Compared with", payload.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_and_text_representations_do_not_both_earn_credit_for_one_source()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "read_image");
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceText("OCR text", "page1");
        call.Comparison.SourceImage("page1", 512, 512);
        Assert.Equal(UsageValueStatus.Unknown, Calculate(call).CloudTokens[UsageCloudTokens.InputSaved].Status);
    }

    [Theory]
    [InlineData(CloudContextPresence.Unknown, "source_context_unknown")]
    [InlineData(CloudContextPresence.NotPresent, null)]
    [InlineData(CloudContextPresence.AlreadyPresent, null)]
    public void Explicit_task_input_does_not_invent_cloud_history(CloudContextPresence presence, string? reason)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceContext(presence);
        call.Comparison.SourceText("This is the bounded context selected for the task.", "selected-input");
        var report = Calculate(call);
        var expected = reason is not null ? UsageValue.Unknown(reason)
            : presence == CloudContextPresence.AlreadyPresent ? UsageValue.Measured(0)
            : O200kCloudTokenProfile.Instance.CountText("This is the bounded context selected for the task.");
        Assert.Equal(expected, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
        Assert.EndsWith(":ExplicitTaskInput", report.Comparison.ScenarioId);
    }
}
