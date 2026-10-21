using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class RuntimeCloudComparisonTests
{
    [Fact]
    public void Long_streams_bound_evidence_and_never_report_partial_baseline_as_complete()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "triage_log");
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        for (var i = 0; i < 16_385; i++) call.Comparison.SourceText("x", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var scenario = Assert.IsType<CloudTokenScenario>(call.Comparison.Scenario("request"));
        Assert.Single(scenario.Sources);
        var report = CloudTokenMethodology.Apply(call.Complete(), scenario, "response", call.Comparison.Profile);
        Assert.Equal(UsageValue.Unknown("source_evidence_limit"), report.CloudTokens[UsageCloudTokens.InputSaved]);
    }

    [Theory]
    [InlineData("<|endoftext|>", 7)]
    [InlineData("abc<|endoftext|>!", 9)]
    [InlineData("<|endofprompt|>", 7)]
    [InlineData("<|endoftext|><|endoftext|>", 13)]
    public void Source_text_that_looks_like_a_reserved_token_is_counted_as_ordinary_text(string text, long expected)
    {
        // Independent tiktoken 0.12.0 o200k_base.encode_ordinary: 27,91,419,1440,919,91,29.
        Assert.Equal(UsageValue.Measured(expected), O200kCloudTokenProfile.Instance.CountText(text));
    }

    [Theory]
    [InlineData(512, 512, "low", 85)]
    [InlineData(512, 512, "high", 255)]
    [InlineData(1024, 1024, "high", 765)]
    [InlineData(2048, 4096, "high", 1105)]
    public void Named_vision_formula_resizes_without_enlarging_small_images(int width, int height, string detail, long expected)
    {
        var profile = O200kCloudTokenProfile.Instance;
        var count = profile.CountImage(new("fixture", width, height, detail));
        Assert.Equal(UsageValueStatus.Estimated, count.Status);
        Assert.Equal(expected, count.Value);
        Assert.Equal(profile.VisionProfileId, count.Method);
    }

    [Fact]
    public void Repeated_source_units_and_context_presence_do_not_earn_duplicate_credit()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        var source = string.Concat(Enumerable.Repeat("One substantial source unit. ", 2000));
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.SourceText(source, "same-file");
        call.Comparison.SourceText(source, "same-file");
        call.Comparison.GeneratedText("Summary");
        var report = Calculate();
        Assert.Equal(O200kCloudTokenProfile.Instance.CountText(source), report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
        Assert.True(report.CloudTokens[UsageCloudTokens.InputSaved].Value > 0);
        call.Comparison.SourceContext(CloudContextPresence.AlreadyPresent);
        report = Calculate();
        Assert.Equal(UsageValue.Measured(0), report.CloudTokens[UsageCloudTokens.InputWithoutDelegation]);
        Assert.True(report.CloudTokens[UsageCloudTokens.InputSaved].Value < 0);
        call.Comparison.SourceContext(CloudContextPresence.Unknown);
        Assert.Equal(UsageValue.Unknown("source_context_unknown"), Calculate().CloudTokens[UsageCloudTokens.InputSaved]);
        UsageReport Calculate() => CloudTokenMethodology.Apply(call.Complete(), call.Comparison.Scenario("request"), "full response", call.Comparison.Profile);
    }

    [Fact]
    public void Complete_payload_and_notice_are_counted_with_signed_conditional_differences()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        call.Comparison.SourceText("small");
        call.Comparison.ContextBasis(CloudContextBasis.ExplicitTaskInput);
        call.Comparison.SourceContext(CloudContextPresence.NotPresent);
        call.Comparison.GeneratedText("answer");
        var scenario = call.Comparison.Scenario("request");
        var payload = UsagePayload.Finalize(call.Complete(), report => UsageNotice.Render(report) + "\n" + new string('x', 1000),
            value => value, scenario, call.Comparison.Profile);
        var expected = call.Comparison.Profile.CountText("request").Value + call.Comparison.Profile.CountText(payload.Value).Value;
        Assert.Equal(expected, payload.Report.CloudTokens[UsageCloudTokens.InputWithDelegation].Value);
        Assert.True(payload.Report.CloudTokens[UsageCloudTokens.InputSaved].Value < 0);
        Assert.Equal(UsageValueStatus.Estimated, payload.Report.CloudTokens[UsageCloudTokens.InputSaved].Status);
        var persisted = UsageReportJson.Write(payload.Report);
        Assert.DoesNotContain("small", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("answer", persisted, StringComparison.Ordinal);
    }
}
