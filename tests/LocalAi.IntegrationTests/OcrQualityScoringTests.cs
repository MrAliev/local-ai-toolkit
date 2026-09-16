using LocalAi.Quality;

namespace LocalAi.IntegrationTests;

public sealed class OcrQualityScoringTests
{
    [Theory]
    [InlineData("Code", "READY")]
    [InlineData("Код", "ГОТОВО")]
    public void Exact_text_has_independent_perfect_content_and_structure_scores(string label, string value)
    {
        var text = $"{label}: {value}\n{label}: {value}";
        var score = OcrQualityScoring.Measure(text, text, true,
            [new(label, value), new(label, value)], 123);
        Assert.True(score.TechnicalCompletion);
        Assert.True(score.HasOutput);
        Assert.Equal(0, score.CharacterErrorRate);
        Assert.Equal(0, score.WordErrorRate);
        Assert.Equal(2, score.ReferenceLineCount);
        Assert.Equal(2, score.OutputLineCount);
        Assert.Equal(1, score.FieldLabelRecall);
        Assert.Equal(1, score.FieldValueAccuracy);
        Assert.Equal(123, score.ElapsedMilliseconds);
    }

    [Fact]
    public void A_completed_call_can_preserve_structure_but_misread_an_annotated_value()
    {
        var score = OcrQualityScoring.Measure("Code: 42\nStatus: READY", "Code: 24\nStatus: READY", true,
            [new("Code", "42"), new("Status", "READY")], 0);
        Assert.True(score.TechnicalCompletion);
        Assert.Equal(2, score.OutputLineCount);
        Assert.Equal(1, score.FieldLabelRecall);
        Assert.Equal(0.5, score.FieldValueAccuracy);
        Assert.True(score.CharacterErrorRate > 0);
        Assert.True(score.WordErrorRate > 0);
    }

    [Fact]
    public void Missing_and_extra_repetitions_are_scored_without_deduplicating_text()
    {
        const string reference = "Stamp: OK\nStamp: OK";
        OcrFieldReference[] fields = [new("Stamp", "OK"), new("Stamp", "OK")];
        var missing = OcrQualityScoring.Measure(reference, "Stamp: OK", true, fields, 0);
        Assert.Equal(1, missing.OutputLineCount);
        Assert.Equal(0.5, missing.FieldLabelRecall);
        Assert.Equal(0.5, missing.FieldValueAccuracy);
        Assert.True(missing.CharacterErrorRate > 0);
        var extra = OcrQualityScoring.Measure(reference, reference + "\n" + reference + "\n" + reference, true, fields, 0);
        Assert.Equal(6, extra.OutputLineCount);
        Assert.True(extra.CharacterErrorRate > 1);
        Assert.True(extra.WordErrorRate > 1);
    }

    [Fact]
    public void Failure_keeps_technical_status_separate_from_unmeasured_quality()
    {
        var score = OcrQualityScoring.Measure("Code: 42", "partial", false, [new("Code", "42")], 987);
        Assert.False(score.TechnicalCompletion);
        Assert.True(score.HasOutput);
        Assert.Null(score.CharacterErrorRate);
        Assert.Null(score.WordErrorRate);
        Assert.Null(score.OutputLineCount);
        Assert.Null(score.FieldLabelRecall);
        Assert.Null(score.FieldValueAccuracy);
        Assert.Equal(987, score.ElapsedMilliseconds);
    }

    [Fact]
    public void A_successful_empty_answer_does_not_receive_perfect_accuracy()
    {
        var score = OcrQualityScoring.Measure("Code: 42", "", true, [new("Code", "42")], 0);
        Assert.True(score.TechnicalCompletion);
        Assert.False(score.HasOutput);
        Assert.Equal(1, score.CharacterErrorRate);
        Assert.Equal(1, score.WordErrorRate);
        Assert.Equal(0, score.OutputLineCount);
        Assert.Equal(0, score.FieldLabelRecall);
        Assert.Equal(0, score.FieldValueAccuracy);
    }

    [Fact]
    public void Character_errors_use_unicode_scalars_and_canonical_unicode_normalization()
    {
        Assert.Equal(0.5, OcrQualityScoring.Measure("🙂a", "🙂b", true, [], 0).CharacterErrorRate);
        var normalized = OcrQualityScoring.Measure("Café\nКод: 42", "Cafe\u0301\r\nКод: 42", true, [], 0);
        Assert.Equal(0, normalized.CharacterErrorRate);
        Assert.Equal(0, normalized.WordErrorRate);
        Assert.Null(normalized.FieldLabelRecall);
        Assert.Null(normalized.FieldValueAccuracy);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t")]
    public void Empty_references_are_invalid_rather_than_a_zero_denominator(string reference)
    {
        Assert.Throws<ArgumentException>(() => OcrQualityScoring.Measure(reference, "text", true, [], 0));
    }

    [Fact]
    public void Field_annotations_must_be_present_in_the_reference_with_the_declared_multiplicity()
    {
        Assert.Throws<ArgumentException>(() => OcrQualityScoring.Measure("Code: 42", "Code: 42", true,
            [new("Code", "99")], 0));
        Assert.Throws<ArgumentException>(() => OcrQualityScoring.Measure("Code: 42", "Code: 42", true,
            [new("Code", "42"), new("Code", "42")], 0));
    }
}
