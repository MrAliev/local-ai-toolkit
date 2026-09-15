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
            _ => string.Join(", ", report.Models.Actual.Select(model => model.Model + (model.Offload switch
            {
                UsageOffload.Partial => " [partial_offload]",
                UsageOffload.Cpu => " [cpu]",
                UsageOffload.Unknown => " [offload_unknown]",
                _ => "",
            })).Distinct(StringComparer.Ordinal)),
        },
        report.Timings[UsageTiming.ElapsedMilliseconds] is { Value: { } ms }
            ? (ms / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " s"
            : Value(report.Timings[UsageTiming.ElapsedMilliseconds]),
        Value(report.CloudTokens[UsageCloudTokens.InputSaved]), Value(report.CloudTokens[UsageCloudTokens.OutputSaved]));

    private static string Value(UsageValue value) => value.Status switch
    {
        UsageValueStatus.Unknown => Catalogue.Format("UsageUnknownReason", value.Reason),
        UsageValueStatus.NotApplicable => Catalogue.Format("UsageNotApplicableReason", value.Reason),
        UsageValueStatus.Estimated => Catalogue.Format("UsageEstimatedValue", Signed(value.Value!.Value)),
        _ => Signed(value.Value!.Value),
    };
    private static string Signed(long value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
}
