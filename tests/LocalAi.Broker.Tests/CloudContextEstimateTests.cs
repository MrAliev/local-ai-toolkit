using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class CloudContextEstimateTests
{
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(-846L, -846)]
    [InlineData((long)int.MinValue, int.MinValue)]
    [InlineData((long)int.MaxValue, int.MaxValue)]
    [InlineData((long)int.MinValue - 1, null)]
    [InlineData((long)int.MaxValue + 1, null)]
    public void Legacy_fields_preserve_signed_values_and_do_not_overflow(long saved, int? expected)
    {
        var report = Report() with { ContextEstimate = new("cloud-agent-context-v1", "fixture", null,
            UsageValue.Measured(Math.Max(0, saved)), UsageValue.Measured(Math.Max(0, -saved)),
            UsageValue.Estimated(saved, "fixture")) };
        Assert.Equal(expected, UsagePayload.LegacySavedTokensEstimate(report));
        Assert.Equal(saved, report.ContextEstimate.SavedTokens.Value);
        Assert.Null(UsagePayload.LegacySavedTokensEstimate(report with { ContextEstimate = null }));
    }

    [Fact]
    public void An_unstable_payload_clears_legacy_estimates_on_its_final_render()
    {
        var final = UsagePayload.Finalize(Report(), report =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { savedTokensEstimate = UsagePayload.LegacySavedTokensEstimate(report) });
            return json.PadRight(report.ContextEstimate?.ReturnedTokens.Value == 100 ? 101 : 100);
        }, value => value, Scenario(CloudContextPresence.Unknown), new Characters());
        Assert.Equal("response_size_unstable", final.Report.ContextEstimate!.SavedTokens.Reason);
        Assert.Contains("\"savedTokensEstimate\":null", final.Value, StringComparison.Ordinal);
    }

    private sealed class Characters : ICloudTokenProfile
    {
        public string TextProfileId => "fixture-characters-v1";
        public string? VisionProfileId => null;
        public UsageValue CountText(string text) => UsageValue.Measured(text.Length);
        public UsageValue CountImage(CloudImageSource image) => UsageValue.Unknown("fixture_no_vision");
    }

    private static UsageReport Report(bool model = true)
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        return call.Complete() with { Models = model
            ? new(UsageModelState.Called, UsageValue.Measured(1), [new("fixture", 1, UsageOffload.None, null)],
                UsageCache.NotApplicable, "fixture") : UsageModels.NoCall() };
    }

    private static CloudTokenScenario Scenario(CloudContextPresence presence, int length = 2000, string request = "") =>
        new("fixture", [new CloudTextSource("input", new string('x', length), Presence: presence)], "", request, "");

    [Theory]
    [InlineData(CloudContextPresence.Unknown)]
    [InlineData(CloudContextPresence.AlreadyPresent)]
    [InlineData(CloudContextPresence.NotPresent)]
    public void The_hypothetical_full_input_is_independent_of_prior_cloud_history(CloudContextPresence presence)
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(presence), "transport", new Characters(), new string('y', 300));
        var estimate = Assert.IsType<CloudContextEstimate>(report.ContextEstimate);
        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal(2000, estimate.InputTokens.Value);
        Assert.Equal(300, estimate.ReturnedTokens.Value);
        Assert.Equal(1700, estimate.SavedTokens.Value);
        Assert.Equal(UsageValueStatus.Estimated, estimate.SavedTokens.Status);
    }

    [Theory]
    [InlineData(100, 160, -60)]
    [InlineData(100, 100, 0)]
    public void Negative_and_zero_context_differences_are_preserved(int input, int output, int saved)
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(CloudContextPresence.Unknown, input), "transport",
            new Characters(), new string('y', output));
        Assert.Equal(saved, report.ContextEstimate!.SavedTokens.Value);
        var parsed = UsageReportJson.Read(UsageReportJson.Write(report));
        Assert.Equal(report.ContextEstimate, parsed.ContextEstimate);
    }

    [Fact]
    public void Instructions_are_input_and_cloud_generation_is_not_added_to_the_difference()
    {
        var scenario = Scenario(CloudContextPresence.Unknown, 100, "task") with { OutputWithoutDelegation = new string('z', 500) };
        var report = CloudTokenMethodology.Apply(Report(), scenario, "transport", new Characters(), new string('y', 20));
        Assert.Equal(104, report.ContextEstimate!.InputTokens.Value);
        Assert.Equal(84, report.ContextEstimate.SavedTokens.Value);
        Assert.Contains("84", UsageNotice.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_and_overlapping_source_ranges_do_not_inflate_the_full_input()
    {
        var scenario = new CloudTokenScenario("fixture", [new CloudTextSource("same", new string('x', 100)),
            new CloudTextSource("same", new string('x', 100)), new CloudTextSource("same", new string('x', 100), 50)], "", "", "");
        var report = CloudTokenMethodology.Apply(Report(), scenario, "transport", new Characters(), new string('y', 20));
        Assert.Equal(150, report.ContextEstimate!.InputTokens.Value);
        Assert.Equal(130, report.ContextEstimate.SavedTokens.Value);
    }

    [Fact]
    public void A_missing_tokenizer_uses_a_named_approximation_for_raw_text()
    {
        var scenario = new CloudTokenScenario("fixture", [new CloudTextSource("source", "abcdefgh")], "", "", "");
        var report = CloudTokenMethodology.Apply(Report(), scenario, "transport", null, "abcd");
        Assert.Equal("weighted-characters-v1", report.ContextEstimate!.TokenizerProfile);
        Assert.Equal(1, report.ContextEstimate.SavedTokens.Value);
    }

    [Fact]
    public void Unsupported_images_do_not_become_zero_tokens()
    {
        var scenario = new CloudTokenScenario("fixture", [new CloudImageSource("image", 100, 100, "high")], "", "", "");
        var report = CloudTokenMethodology.Apply(Report(), scenario, "transport", new Characters(), "answer");
        Assert.Equal(UsageValueStatus.Unknown, report.ContextEstimate!.InputTokens.Status);
        Assert.Null(report.ContextEstimate.SavedTokens.Value);
    }

    [Fact]
    public void A_missing_cloud_history_or_legacy_baseline_does_not_discard_observed_input()
    {
        var comparison = new RuntimeCloudComparison();
        comparison.UnknownBaseline("retrieval_baseline_not_established");
        comparison.SourceText("Observed input", "source");
        var report = CloudTokenMethodology.Apply(Report(), comparison.Scenario("task"), "transport", comparison.Profile, "answer");
        var expected = comparison.Profile.CountText("Observed input").Value + comparison.Profile.CountText("task").Value
            - comparison.Profile.CountText("answer").Value;
        Assert.Equal(expected, report.ContextEstimate!.SavedTokens.Value);
        Assert.Equal("retrieval_baseline_not_established", report.CloudTokens[UsageCloudTokens.InputSaved].Reason);
    }

    [Fact]
    public void A_model_that_was_not_called_does_not_get_a_new_context_estimate()
    {
        var report = CloudTokenMethodology.Apply(Report(false), Scenario(CloudContextPresence.Unknown), "answer", new Characters());
        Assert.Null(report.ContextEstimate);
    }

    [Fact]
    public void Old_records_keep_their_original_fields_and_do_not_gain_a_fabricated_estimate()
    {
        var report = Report(false);
        var json = UsageReportJson.Write(report);
        Assert.DoesNotContain("contextEstimate", json, StringComparison.Ordinal);
        Assert.Null(UsageReportJson.Read(json).ContextEstimate);
    }

    [Fact]
    public void Final_visible_output_includes_the_notice_and_all_returned_text()
    {
        var report = Report();
        var result = UsagePayload.Finalize(report, value => UsageNotice.Render(value) + "\nanswer", value => value,
            Scenario(CloudContextPresence.Unknown, 5000), new Characters());
        Assert.Equal(result.Value.Length, result.Report.ContextEstimate!.ReturnedTokens.Value);
        Assert.Equal(5000 - result.Value.Length, result.Report.ContextEstimate.SavedTokens.Value);
        Assert.DoesNotContain("cannot be estimated", result.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Persisted_estimates_reject_an_inconsistent_difference()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(CloudContextPresence.Unknown), "answer", new Characters());
        Assert.Throws<System.Text.Json.JsonException>(() => UsageReportJson.Write(report with
        {
            ContextEstimate = report.ContextEstimate! with { SavedTokens = UsageValue.Estimated(999, "fixture") }
        }));
    }

    [Fact]
    public void A_later_legacy_reason_cannot_hide_incomplete_real_input()
    {
        var comparison = new RuntimeCloudComparison();
        comparison.SourceText("a", "same");
        comparison.SourceText("bb", "same");
        comparison.UnknownBaseline("retrieval_baseline_not_established");
        var report = CloudTokenMethodology.Apply(Report(), comparison.Scenario("task"), "answer", comparison.Profile);
        Assert.Equal("source_observation_overlap_unknown", report.ContextEstimate!.InputTokens.Reason);
        Assert.Null(report.ContextEstimate.SavedTokens.Value);
    }

    [Fact]
    public void A_missing_source_is_different_from_an_explicit_empty_source()
    {
        var comparison = new RuntimeCloudComparison();
        comparison.UnknownBaseline("retrieval_baseline_not_established");
        var missing = CloudTokenMethodology.Apply(Report(), comparison.Scenario("task"), "answer", comparison.Profile);
        Assert.Equal("source_reading_unknown", missing.ContextEstimate!.InputTokens.Reason);
        comparison.NoSource();
        var empty = CloudTokenMethodology.Apply(Report(), comparison.Scenario("task"), "answer", comparison.Profile);
        Assert.NotNull(empty.ContextEstimate!.SavedTokens.Value);
    }

    [Theory]
    [InlineData(UsageDelivery.Failed)]
    [InlineData(UsageDelivery.NotAttempted)]
    public void Undelivered_output_is_not_fabricated_context_savings(UsageDelivery delivery)
    {
        var baseline = Report();
        var report = CloudTokenMethodology.Apply(baseline with
        {
            Outcome = baseline.Outcome with { Delivery = delivery, DeliveryReason = "fixture" }
        }, Scenario(CloudContextPresence.Unknown), "answer", new Characters());
        Assert.Equal(2000, report.ContextEstimate!.InputTokens.Value);
        Assert.Equal("result_not_delivered", report.ContextEstimate.ReturnedTokens.Reason);
        Assert.Null(report.ContextEstimate.SavedTokens.Value);
    }

    [Fact]
    public void Accounting_is_independent_of_the_quality_of_a_delivered_answer()
    {
        var baseline = Report();
        var report = CloudTokenMethodology.Apply(baseline with
        {
            Outcome = baseline.Outcome with { Delivery = UsageDelivery.Delivered, DeliveryReason = null,
                Technical = UsageTechnicalOutcome.Failed, TechnicalReason = "fixture",
                Validation = UsageValidation.Failed, ValidationReason = "fixture" }
        }, Scenario(CloudContextPresence.Unknown), "answer", new Characters());
        Assert.Equal(1994, report.ContextEstimate!.SavedTokens.Value);
    }

    [Fact]
    public void Persisted_context_estimates_cannot_claim_a_model_call_that_did_not_happen()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(CloudContextPresence.Unknown), "answer", new Characters());
        Assert.Throws<System.Text.Json.JsonException>(() => UsageReportJson.Write(report with { Models = UsageModels.NoCall() }));
        Assert.Throws<System.Text.Json.JsonException>(() => UsageReportJson.Write(report with { SchemaVersion = 1 }));
        Assert.Throws<System.Text.Json.JsonException>(() => UsageReportJson.Write(report with
        {
            Outcome = report.Outcome with { Delivery = UsageDelivery.Failed, DeliveryReason = "fixture" }
        }));
    }
}
