using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class UsagePayloadTests
{
    [Theory]
    [InlineData("en", 1000, -40, "Approximately 960 cloud tokens saved.")]
    [InlineData("ru", 1000, -40, "Сэкономлено примерно 960 облачных токенов.")]
    [InlineData("en", 0, -41, "No token savings; approximately 41 additional cloud tokens.")]
    [InlineData("ru", 0, -41, "Экономии нет; дополнительно примерно 41 облачных токенов.")]
    [InlineData("ru", 41, -41, "Экономии облачных токенов нет.")]
    [InlineData("en", long.MaxValue, long.MaxValue, "Approximately 18446744073709551614 cloud tokens saved.")]
    [InlineData("en", long.MinValue, 0, "No token savings; approximately 9223372036854775808 additional cloud tokens.")]
    public void Notice_gives_one_plain_language_estimate(string language, long input, long output, string expected)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
            var report = call.Complete();
            var tokens = report.CloudTokens.ToDictionary();
            tokens[UsageCloudTokens.InputSaved] = UsageValue.Estimated(input, "fixture");
            tokens[UsageCloudTokens.OutputSaved] = UsageValue.Measured(output);
            report = report with { CloudTokens = tokens };
            var notice = UsageNotice.Render(report);
            Assert.Contains(expected, notice, StringComparison.Ordinal);
            Assert.DoesNotContain("INPUT", notice, StringComparison.Ordinal);
            Assert.DoesNotContain("OUTPUT", notice, StringComparison.Ordinal);
            Assert.Equal(input, report.CloudTokens[UsageCloudTokens.InputSaved].Value);
            Assert.Equal(output, report.CloudTokens[UsageCloudTokens.OutputSaved].Value);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
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
    [InlineData("en", "Token savings cannot be estimated.", "No model call")]
    [InlineData("ru", "Экономию токенов оценить нельзя.", "Модель не вызывалась")]
    public void Partial_evidence_does_not_claim_a_total(string language, string unknown, string notCalled)
    {
        Assert.Empty(UsageNotice.Catalogue.Gaps());
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
            var report = call.Complete();
            var tokens = report.CloudTokens.ToDictionary();
            tokens[UsageCloudTokens.InputSaved] = UsageValue.Estimated(4200, "fixture");
            var notice = UsageNotice.Render(report with { CloudTokens = tokens });
            Assert.Contains(unknown, notice, StringComparison.Ordinal);
            Assert.DoesNotContain("4200", notice, StringComparison.Ordinal);
            Assert.Contains(notCalled, notice, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
    [Theory]
    [InlineData("en", "| models: Loaded without a task (qwen3.5:9b [partial_offload]) |")]
    [InlineData("ru", "| модели: Загрузка без задачи (qwen3.5:9b [partial_offload]) |")]
    public void A_load_without_a_task_is_named_before_the_model_it_loaded(string language, string segment)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "local_model_preflight");
            call.ModelLoaded("qwen3.5:9b", UsageOffload.Partial);
            Assert.Contains(segment, UsageNotice.Render(call.Complete()), StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
