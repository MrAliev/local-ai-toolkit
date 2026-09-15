using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class CloudTokenMethodologyTests
{
    private static UsageReport Report() => UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "locallm", "ask_local", Guid.NewGuid()) with
    {
        Outcome = new(UsageTechnicalOutcome.Succeeded, UsageValidation.Passed, UsageDelivery.Delivered, null, null, null),
    };

    private static CloudTokenScenario Scenario(params CloudSource[] sources) => new("same-result-v1", sources, "answer", "", "call");
    private static CloudTextSource Unseen(string id, string text, int offset = 0) => new(id, text, offset, CloudContextPresence.NotPresent);

    [Fact]
    public void The_complete_tool_payload_is_counted_once_including_wrappers_and_notice()
    {
        const string payload = "<untrusted-content>answer</untrusted-content>\nnotice";
        var profile = new CharacterProfile();
        var report = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("file@revision", "abcdefgh")), payload, profile);
        Assert.Equal(8 - payload.Length, report.CloudTokens[UsageCloudTokens.InputSaved].Value);
        Assert.Equal(2, report.CloudTokens[UsageCloudTokens.OutputSaved].Value);
        Assert.Equal(payload.Length, report.Volumes[UsageVolume.ResponseCharacters].Value);
        Assert.Equal(1, profile.CountedTexts.Count(text => text == payload));
        Assert.Equal(CloudTokenMethodology.Version, report.MethodologyVersion);
        Assert.Equal(profile.TextProfileId, report.Comparison.TokenizerProfile);
        _ = UsageReportJson.Read(UsageReportJson.Write(report));
    }

    [Fact]
    public void Already_transmitted_sources_cannot_be_credited_again()
    {
        var known = new CloudTextSource("file@revision", "abcdefgh", 0, CloudContextPresence.AlreadyPresent);
        var report = CloudTokenMethodology.Apply(Report(), Scenario(known), "result", new CharacterProfile());
        Assert.Equal(0, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
        Assert.Equal(-6, report.CloudTokens[UsageCloudTokens.InputSaved].Value);
    }

    [Fact]
    public void A_path_without_context_provenance_does_not_prove_saved_reading()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(new CloudTextSource("file@revision", "abcdefgh")), "result", new CharacterProfile());
        AssertUnknown(report.CloudTokens[UsageCloudTokens.InputSaved], "source_context_unknown");
        Assert.Equal(6, report.Volumes[UsageVolume.ResponseBytes].Value);
    }

    [Fact]
    public void Repeated_and_overlapping_chunks_are_counted_once()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("file@revision", "abcdef"), Unseen("file@revision", "cdefgh", 2),
            Unseen("file@revision", "abcdef")), "", new CharacterProfile());
        Assert.Equal(8, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
    }

    [Fact]
    public void Previously_retrieved_parts_are_removed_from_a_later_whole_file_comparison()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("file@revision", "abcdefgh"),
            new CloudTextSource("file@revision", "cdef", 2, CloudContextPresence.AlreadyPresent)), "", new CharacterProfile());
        Assert.Equal(4, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
    }

    [Fact]
    public void Conflicting_overlapping_snapshots_are_unknown_instead_of_double_counted()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("file@revision", "abcdef"), Unseen("file@revision", "different", 2)), "", new CharacterProfile());
        AssertUnknown(report.CloudTokens[UsageCloudTokens.InputSaved], "source_content_conflict");
    }

    [Theory]
    [InlineData(UsageDelivery.Failed)]
    [InlineData(UsageDelivery.NotAttempted)]
    [InlineData(UsageDelivery.Unknown)]
    public void Undelivered_generation_is_not_claimed_as_a_replacement(UsageDelivery delivery)
    {
        var report = Report();
        report = CloudTokenMethodology.Apply(report with { Outcome = report.Outcome with { Delivery = delivery, DeliveryReason = "write_failed" } },
            Scenario(), "failure notice", new CharacterProfile());
        AssertUnknown(report.CloudTokens[UsageCloudTokens.OutputSaved], "result_not_delivered");
        Assert.Equal(-14, report.CloudTokens[UsageCloudTokens.InputSaved].Value);
    }

    [Fact]
    public void A_file_delivery_only_removes_the_returned_document_not_the_confirmation_payload()
    {
        var returned = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("source", "abcdefgh")), "answer+notice", new CharacterProfile());
        var written = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("source", "abcdefgh")), "saved", new CharacterProfile());
        Assert.Equal(3, written.CloudTokens[UsageCloudTokens.InputSaved].Value);
        Assert.Equal(returned.CloudTokens[UsageCloudTokens.OutputSaved], written.CloudTokens[UsageCloudTokens.OutputSaved]);
        Assert.Equal(-5, returned.CloudTokens[UsageCloudTokens.InputSaved].Value);
    }

    [Fact]
    public void Images_require_a_named_vision_profile_instead_of_a_universal_area_formula()
    {
        var scenario = Scenario(new CloudImageSource("image@revision", 800, 600, "high", CloudContextPresence.NotPresent));
        var unknown = CloudTokenMethodology.Apply(Report(), scenario, "answer", HeuristicCloudTokenProfile.Instance);
        AssertUnknown(unknown.CloudTokens[UsageCloudTokens.InputSaved], "vision_profile_unknown");
        var known = CloudTokenMethodology.Apply(Report(), scenario, "answer", new CharacterProfile());
        Assert.Equal(7, known.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
        Assert.Equal("test-vision-v1", known.Comparison.VisionProfile);
    }

    [Fact]
    public void Repeated_images_and_images_already_in_context_are_not_double_counted()
    {
        var image = new CloudImageSource("image@revision", 800, 600, "high", CloudContextPresence.NotPresent);
        var report = CloudTokenMethodology.Apply(Report(), Scenario(image, image), "", new CharacterProfile());
        Assert.Equal(7, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
        report = CloudTokenMethodology.Apply(Report(), Scenario(image, image with { Presence = CloudContextPresence.AlreadyPresent }), "", new CharacterProfile());
        Assert.Equal(0, report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Value);
    }

    [Fact]
    public void Heuristic_results_name_the_method_without_inventing_a_confidence_interval()
    {
        var report = CloudTokenMethodology.Apply(Report(), Scenario(Unseen("source", new string('a', 400))), "ответ", HeuristicCloudTokenProfile.Instance);
        var value = report.CloudTokens[UsageCloudTokens.InputSaved];
        Assert.Equal(UsageValueStatus.Estimated, value.Status);
        Assert.Null(value.Range);
        Assert.Equal("not_calibrated", value.RangeReason);
        Assert.False(string.IsNullOrWhiteSpace(value.Method));
        _ = UsageReportJson.Read(UsageReportJson.Write(report));
    }

    [Fact]
    public void Missing_scenario_or_profile_keeps_observed_response_sizes()
    {
        var withoutScenario = CloudTokenMethodology.Apply(Report(), null, "Ж", new CharacterProfile());
        Assert.All(withoutScenario.CloudTokens.Values, value => AssertUnknown(value, "comparison_scenario_unknown"));
        Assert.Equal(2, withoutScenario.Volumes[UsageVolume.ResponseBytes].Value);
        Assert.Equal(1, withoutScenario.Volumes[UsageVolume.ResponseCharacters].Value);
        var withoutProfile = CloudTokenMethodology.Apply(Report(), Scenario(), "Ж", null);
        Assert.All(withoutProfile.CloudTokens.Values, value => AssertUnknown(value, "text_profile_unknown"));
    }

    [Fact]
    public void Missing_payload_is_unknown_and_empty_payload_is_a_known_zero()
    {
        var missing = CloudTokenMethodology.Apply(Report(), Scenario(), null, new CharacterProfile());
        AssertUnknown(missing.CloudTokens[UsageCloudTokens.InputWithDelegation], "full_response_unknown");
        var empty = CloudTokenMethodology.Apply(Report(), Scenario(), "", new CharacterProfile());
        Assert.Equal(0, empty.CloudTokens[UsageCloudTokens.InputWithDelegation].Value);
        Assert.Equal(0, empty.CloudTokens[UsageCloudTokens.InputSaved].Value);
    }

    [Fact]
    public void Cloud_calculations_do_not_overwrite_observed_backend_tokens_or_times()
    {
        var report = Report();
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.BackendPromptTokens] = UsageValue.Measured(9999);
        report = CloudTokenMethodology.Apply(report with { Volumes = volumes }, Scenario(), "response", new CharacterProfile());
        Assert.Equal(9999, report.Volumes[UsageVolume.BackendPromptTokens].Value);
        Assert.Equal(UsageValueStatus.Unknown, report.Timings[UsageTiming.ElapsedMilliseconds].Status);
        Assert.Equal(-8, report.CloudTokens[UsageCloudTokens.InputSaved].Value);
    }

    [Theory]
    [InlineData(UsageTechnicalOutcome.Failed, UsageValidation.Passed)]
    [InlineData(UsageTechnicalOutcome.Succeeded, UsageValidation.Failed)]
    [InlineData(UsageTechnicalOutcome.Succeeded, UsageValidation.Unknown)]
    public void Failed_or_unvalidated_answers_are_not_credited_as_replaced_generation(UsageTechnicalOutcome technical, UsageValidation validation)
    {
        var report = Report();
        report = CloudTokenMethodology.Apply(report with
        {
            Outcome = report.Outcome with { Technical = technical, Validation = validation },
        }, Scenario(), "unvalidated answer", new CharacterProfile());
        AssertUnknown(report.CloudTokens[UsageCloudTokens.OutputSaved], "result_not_validated");
    }

    [Fact]
    public void An_administrative_operation_stays_inapplicable_even_when_a_scenario_is_supplied()
    {
        var report = UsageReportingRegistry.CreateReport(UsageSurface.Mcp, "codesearch", "index_unload", Guid.NewGuid());
        report = CloudTokenMethodology.Apply(report, Scenario(Unseen("source", "abcdefgh")), "done", new CharacterProfile());
        Assert.All(report.CloudTokens.Values, value => Assert.Equal(UsageValueStatus.NotApplicable, value.Status));
        Assert.Equal(4, report.Volumes[UsageVolume.ResponseBytes].Value);
        _ = UsageReportJson.Read(UsageReportJson.Write(report));
    }

    private static void AssertUnknown(UsageValue value, string reason)
    {
        Assert.Equal(UsageValueStatus.Unknown, value.Status);
        Assert.Null(value.Value);
        Assert.Equal(reason, value.Reason);
    }

    // A deterministic counting profile exercises the comparison engine independently of the
    // production heuristic. It deliberately counts characters, not a real vendor tokenizer.
    private sealed class CharacterProfile : ICloudTokenProfile
    {
        public List<string> CountedTexts { get; } = [];
        public string TextProfileId => "test-characters-v1";
        public string? VisionProfileId => "test-vision-v1";
        public UsageValue CountText(string text) { CountedTexts.Add(text); return UsageValue.Measured(text.Length); }
        public UsageValue CountImage(CloudImageSource image) => UsageValue.Measured(7);
    }
}
