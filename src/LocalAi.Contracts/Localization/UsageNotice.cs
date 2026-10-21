using System.Globalization;

namespace LocalAi.Contracts.Localization;

public static class UsageNotice
{
    public static TextCatalogue Catalogue { get; } = new("LocalAi.Contracts.Localization.UsageNotice", typeof(UsageNotice).Assembly);
    public static string SaveFailed(Guid callId, string code) => Catalogue.Format("UsageReportSaveFailed", callId, code);

    public static string Render(UsageReport report) => Catalogue.Format("UsageNotice",
        report.Identity.CallId,
        report.Models.State switch
        {
            UsageModelState.NotCalled => Catalogue.Get("UsageModelNotCalled"),
            UsageModelState.Unknown => Catalogue.Get("UsageUnknown"),
            UsageModelState.Called => Models(report),
            UsageModelState.Loaded => Catalogue.Format("UsageModelLoaded", Models(report)),
            _ => throw new ArgumentOutOfRangeException(nameof(report), report.Models.State, null),
        },
        report.Timings[UsageTiming.ElapsedMilliseconds] is { Value: { } ms }
            ? (ms / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " s"
            : Value(report.Timings[UsageTiming.ElapsedMilliseconds]),
        Savings(report)) + Scenario(report);

    private static string Savings(UsageReport report)
    {
        if (report.Comparison.ScenarioId?.StartsWith("runtime-request-context-v2:", StringComparison.Ordinal) == true
            && report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Reason is
                "retrieval_baseline_not_established" or "source_observation_overlap_unknown" or "request_context_not_declared")
            return report.CloudTokens[UsageCloudTokens.InputWithDelegation].Value is { } context
                ? Catalogue.Format("UsageRequiredContextUnknown", context.ToString("0", CultureInfo.InvariantCulture))
                : Catalogue.Get("UsageSavingsUnknown");
        var input = report.CloudTokens[UsageCloudTokens.InputSaved];
        var output = report.CloudTokens[UsageCloudTokens.OutputSaved];
        if (input.Status == UsageValueStatus.NotApplicable && output.Status == UsageValueStatus.NotApplicable)
            return Catalogue.Get("UsageSavingsNotApplicable");
        if (input.Status is not (UsageValueStatus.Measured or UsageValueStatus.Estimated)
            || output.Status is not (UsageValueStatus.Measured or UsageValueStatus.Estimated)
            || input.Value is null || output.Value is null)
            return Catalogue.Get("UsageSavingsUnknown");

        // Input and generated output are distinct token categories in one comparison.
        // Decimal avoids overflow when combining signed long counts or taking their magnitude.
        var saved = (decimal)input.Value.Value + output.Value.Value;
        var amount = Math.Abs(saved).ToString("0", CultureInfo.InvariantCulture);
        return saved > 0 ? Catalogue.Format("UsageSavingsPositive", amount)
            : saved < 0 ? Catalogue.Format("UsageSavingsAdditional", amount)
            : Catalogue.Get("UsageSavingsZero");
    }

    private static string Models(UsageReport report) =>
        string.Join(", ", report.Models.Actual.Select(model => model.Model + (model.Offload switch
        {
            UsageOffload.Partial => " [partial_offload]",
            UsageOffload.Cpu => " [cpu]",
            UsageOffload.Unknown => " [offload_unknown]",
            _ => "",
        })).Distinct(StringComparer.Ordinal));

    private static string Scenario(UsageReport report)
    {
        if (report.CloudTokens[UsageCloudTokens.InputWithoutDelegation].Reason is
            "retrieval_baseline_not_established" or "source_observation_overlap_unknown" or "request_context_not_declared") return string.Empty;
        var prefix = report.Comparison.ScenarioId?.StartsWith("runtime-request-context-v2:", StringComparison.Ordinal) == true
            ? "runtime-request-context-v2:" : "runtime-source-units-v1:";
        if (report.Comparison.ScenarioId?.StartsWith(prefix, StringComparison.Ordinal) != true) return string.Empty;
        var key = report.Comparison.ScenarioId[prefix.Length..].Split(':')[0] switch
        {
            nameof(CloudContextPresence.NotPresent) => prefix == "runtime-request-context-v2:"
                ? "UsageRequestContextScenario" : "UsageFirstReadScenario",
            nameof(CloudContextPresence.AlreadyPresent) => "UsageAlreadyReadScenario",
            _ => "UsageUnknownContextScenario",
        };
        return " " + Catalogue.Format(key, report.Comparison.TokenizerProfile, report.Comparison.VisionProfile);
    }

    private static string Value(UsageValue value, string? profile = null) => value.Status switch
    {
        UsageValueStatus.Unknown => Catalogue.Format("UsageUnknownReason", value.Reason),
        UsageValueStatus.NotApplicable => Catalogue.Format("UsageNotApplicableReason", value.Reason),
        UsageValueStatus.Estimated when profile == HeuristicCloudTokenProfile.Instance.TextProfileId =>
            Catalogue.Format("UsageHeuristicValue", Signed(value.Value!.Value)),
        UsageValueStatus.Estimated => Catalogue.Format("UsageEstimatedValue", Signed(value.Value!.Value)),
        UsageValueStatus.Measured when !string.IsNullOrWhiteSpace(profile) && profile != HeuristicCloudTokenProfile.Instance.TextProfileId =>
            Catalogue.Format("UsageTokenizerValue", Signed(value.Value!.Value), profile),
        _ => Catalogue.Format("UsageMeasuredValue", Signed(value.Value!.Value)),
    };
    private static string Signed(long value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
}
