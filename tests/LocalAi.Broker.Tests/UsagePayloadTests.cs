using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class UsagePayloadTests
{
    [Fact]
    public void A_nonconverging_payload_has_a_bounded_calculation_and_unknown_size()
    {
        using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
        var renders = 0;
        var result = UsagePayload.Finalize(call.Complete(), report =>
        {
            renders++;
            return new string('x', (int)(report.Volumes[UsageVolume.ResponseBytes].Value ?? 0) + 1);
        }, value => value);
        Assert.InRange(renders, 2, 17);
        Assert.Equal(UsageValue.Unknown("response_size_unstable"), result.Report.Volumes[UsageVolume.ResponseBytes]);
    }

    [Theory]
    [InlineData("en", "Estimated", "Unknown", "No model call")]
    [InlineData("ru", "Оценка:", "Неизвестно", "Модель не вызывалась")]
    public void Notice_keeps_signed_separate_counts_and_unknown_values_in_both_languages(string language, string estimated, string unknown, string notCalled)
    {
        Assert.Empty(UsageNotice.Catalogue.Gaps());
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
            var report = call.Complete();
            var tokens = report.CloudTokens.ToDictionary();
            tokens[UsageCloudTokens.InputSaved] = UsageValue.Estimated(-42, "fixture");
            tokens[UsageCloudTokens.OutputSaved] = UsageValue.Unknown("comparison_not_recorded");
            var notice = UsageNotice.Render(report with { CloudTokens = tokens });
            Assert.Contains("INPUT " + estimated, notice, StringComparison.Ordinal);
            Assert.Contains("-42", notice, StringComparison.Ordinal);
            Assert.Contains("OUTPUT " + unknown, notice, StringComparison.Ordinal);
            Assert.Contains(notCalled, notice, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
