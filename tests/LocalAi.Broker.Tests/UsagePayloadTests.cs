using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Contracts.Localization;

namespace LocalAi.Broker.Tests;

public sealed class UsagePayloadTests
{
    [Theory]
    [InlineData("en", false, "Measured", "Tokenizer tiktoken-0.12.0:cl100k_base:")]
    [InlineData("ru", false, "Измерено:", "Токенизатор tiktoken-0.12.0:cl100k_base:")]
    [InlineData("en", true, "Estimated", "Heuristic estimate")]
    [InlineData("ru", true, "Оценка:", "Эвристическая оценка:")]
    public void Mixed_vision_input_does_not_name_only_the_text_counting_method(string language, bool heuristic, string inputLabel, string outputLabel)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "read_image");
            var report = call.Complete();
            var tokens = report.CloudTokens.ToDictionary();
            tokens[UsageCloudTokens.InputSaved] = heuristic ? UsageValue.Estimated(41, "fixture-sum") : UsageValue.Measured(41);
            tokens[UsageCloudTokens.OutputSaved] = tokens[UsageCloudTokens.InputSaved];
            var notice = UsageNotice.Render(report with
            {
                CloudTokens = tokens,
                Comparison = new("fixture", heuristic ? "weighted-characters-v1" : "tiktoken-0.12.0:cl100k_base", "fixture-vision-v1"),
            });
            Assert.Contains("INPUT " + inputLabel + " +41", notice, StringComparison.Ordinal);
            Assert.Contains("OUTPUT " + outputLabel + " +41", notice, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Theory]
    [InlineData("en", "tiktoken-0.12.0:cl100k_base", "Tokenizer tiktoken-0.12.0:cl100k_base:")]
    [InlineData("ru", "tiktoken-0.12.0:cl100k_base", "Токенизатор tiktoken-0.12.0:cl100k_base:")]
    [InlineData("en", null, "Measured")]
    [InlineData("ru", null, "Измерено:")]
    [InlineData("en", "weighted-characters-v1", "Measured")]
    [InlineData("ru", "weighted-characters-v1", "Измерено:")]
    public void Measured_counts_name_only_an_actual_tokenizer_profile(string language, string? profile, string label)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
            var report = call.Complete();
            var tokens = report.CloudTokens.ToDictionary();
            tokens[UsageCloudTokens.InputSaved] = UsageValue.Measured(0);
            tokens[UsageCloudTokens.OutputSaved] = UsageValue.Measured(-41);
            var notice = UsageNotice.Render(report with { Comparison = new("fixture", profile, null), CloudTokens = tokens });
            Assert.Contains("INPUT " + label + " 0", notice, StringComparison.Ordinal);
            Assert.Contains("OUTPUT " + label + " -41", notice, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Theory]
    [InlineData("en", "Heuristic estimate")]
    [InlineData("ru", "Эвристическая оценка:")]
    public void Character_heuristics_are_identified_in_the_compact_notice(string language, string label)
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            using var call = new UsageCall(UsageSurface.Mcp, "locallm", "ask_local");
            var scenario = new CloudTokenScenario("fixture", [new CloudTextSource("synthetic", "Пример текста", 0, CloudContextPresence.NotPresent)],
                "answer", "", "");
            var report = CloudTokenMethodology.Apply(call.Complete(), scenario, "a longer response with report overhead", HeuristicCloudTokenProfile.Instance);
            var notice = UsageNotice.Render(report);
            Assert.Contains("INPUT " + label + " -", notice, StringComparison.Ordinal);
            Assert.Contains("OUTPUT " + label + " +", notice, StringComparison.Ordinal);
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
