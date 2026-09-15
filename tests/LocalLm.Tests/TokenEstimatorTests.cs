using LocalLm.Core;

namespace LocalLm.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void CyrillicCostsRoughlyTwiceAsManyTokensAsLatinForTheSameLength()
    {
        var latin = new string('a', 1000);
        var cyrillic = new string('ф', 1000);

        var latinTokens = TokenEstimator.ForText(latin);
        var cyrillicTokens = TokenEstimator.ForText(cyrillic);

        // This is the whole reason the estimator inspects the text: assuming the Latin ratio on
        // this codebase's Russian comments and commit messages would understate savings by ~half.
        Assert.True(cyrillicTokens > latinTokens * 1.5,
            $"expected Cyrillic to cost meaningfully more, got {cyrillicTokens} vs {latinTokens}");
    }

    [Fact]
    public void MixedTextLandsBetweenThePureCases()
    {
        var mixed = string.Concat(Enumerable.Repeat("public void Закрыть() // закрывает заказ\n", 25));
        var tokens = TokenEstimator.ForText(mixed);

        Assert.InRange(tokens, mixed.Length / 4, mixed.Length / 2);
    }

    [Fact]
    public void EmptyTextCostsNothing()
    {
        Assert.Equal(0, TokenEstimator.ForText(string.Empty));
        Assert.Equal(0, TokenEstimator.ForText(null!));
    }

    [Fact]
    public void ImageCostScalesWithPixelsNotFileSize()
    {
        var small = TokenEstimator.ForImage(new ImageInfo(800, 600, "png"));
        var large = TokenEstimator.ForImage(new ImageInfo(1600, 1200, "png"));

        Assert.Equal(small * 4, large);
        Assert.InRange(TokenEstimator.ForImage(new ImageInfo(1920, 1080, "png")), 2000, 3500);
    }

    [Fact]
    public void SavingSubtractsTheAnswerAndPreservesAnIncrease()
    {
        Assert.Equal(-990, TokenEstimator.Saved(10, new string('a', 4000)));

        var saved = TokenEstimator.Saved(50_000, "короткий ответ");
        Assert.InRange(saved, 49_000, 50_000);
    }

    [Fact]
    public void Translation_metrics_separate_local_work_generation_and_context_delta()
    {
        var metrics = TokenEstimator.ForTranslation(
            new string('a', 4_000),
            new string('ф', 2_200),
            TranslationDelivery.ToCaller);

        Assert.Equal(1_000, metrics.InputTokens);
        Assert.Equal(1_000, metrics.OutputTokens);
        Assert.Equal(2_000, metrics.LocalTokensProcessed);
        Assert.Equal(1_000, metrics.EstimatedCloudGenerationTokensSaved);
        Assert.Equal(0, metrics.EstimatedNetCloudContextTokensSaved);
    }

    /// <summary>
    /// Generation is already counted separately. Writing a translation to a file does not
    /// turn the avoided output into additional input savings.
    /// </summary>
    [Fact]
    public void A_translation_written_to_a_file_does_not_count_generation_twice()
    {
        var source = new string('a', 4_000);
        var translated = new string('ф', 2_200);

        var written = TokenEstimator.ForTranslation(
            source,
            translated,
            TranslationDelivery.ToFile);

        Assert.Equal(1_000, written.EstimatedNetCloudContextTokensSaved);
        Assert.Equal(2_000, written.EstimatedNetCloudContextTokensSaved + written.EstimatedCloudGenerationTokensSaved);

        // Everything else is the same work and the same arithmetic; only where the document ends
        // up differs.
        var returned = TokenEstimator.ForTranslation(
            source,
            translated,
            TranslationDelivery.ToCaller);
        Assert.Equal(returned.InputTokens, written.InputTokens);
        Assert.Equal(returned.OutputTokens, written.OutputTokens);
        Assert.Equal(
            returned.EstimatedCloudGenerationTokensSaved,
            written.EstimatedCloudGenerationTokensSaved);
    }

    [Fact]
    public void Translation_preserves_a_negative_input_difference()
    {
        var metrics = TokenEstimator.ForTranslation("abcd", new string('a', 400), TranslationDelivery.ToCaller);
        Assert.Equal(-99, metrics.EstimatedNetCloudContextTokensSaved);
    }

    [Theory]
    [InlineData(100, "~100")]
    [InlineData(-100, "~-100")]
    [InlineData(20_000, "~20K")]
    [InlineData(250_000, "~250K")]
    public void Describe_reports_a_signed_point_estimate(int saved, string expected)
    {
        var text = TokenEstimator.Describe(saved);

        Assert.Equal(expected, text);
        Assert.DoesNotContain("–", text);
    }

    [Fact]
    public void A_longer_response_reports_an_increase()
    {
        var image = TokenEstimator.ForImage(new ImageInfo(336, 52, "png"));
        var saved = TokenEstimator.Saved(image, new string('a', 4_000));

        var sentence = TokenEstimator.DescribeSaving(saved);

        Assert.True(saved < 0);
        Assert.Contains("Estimated increase in cloud tokens:", sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_but_real_saving_keeps_its_value()
    {
        var sentence = TokenEstimator.DescribeSaving(100);

        Assert.Equal("Estimated cloud token savings: ~100.", sentence);
    }

    [Fact]
    public void A_large_estimate_does_not_invent_a_confidence_interval()
    {
        var sentence = TokenEstimator.DescribeSaving(20_000);

        Assert.Equal("Estimated cloud token savings: ~20K.", sentence);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "~1")]
    [InlineData(-1, "~-1")]
    [InlineData(1250, "~1.3K")]
    [InlineData(999500, "~1M")]
    [InlineData(long.MinValue, "~-9223372036855M")]
    public void Point_formatting_preserves_sign_and_handles_extreme_counts(long value, string expected) =>
        Assert.Equal(expected, TokenEstimator.DescribeRange(value));

    [Fact]
    public void Zero_does_not_invent_a_reason_for_the_result() =>
        Assert.Equal("Estimated change in cloud tokens: 0.", TokenEstimator.DescribeSaving(0));
}
