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
        Value(report.CloudTokens[UsageCloudTokens.InputSaved], report.Comparison.VisionProfile is null ? report.Comparison.TokenizerProfile : null),
        Value(report.CloudTokens[UsageCloudTokens.OutputSaved], report.Comparison.TokenizerProfile)) + Scenario(report);

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
        const string prefix = "runtime-source-units-v1:";
        if (report.Comparison.ScenarioId?.StartsWith(prefix, StringComparison.Ordinal) != true) return string.Empty;
        var key = report.Comparison.ScenarioId[prefix.Length..] switch
        {
            nameof(CloudContextPresence.NotPresent) => "UsageFirstReadScenario",
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
