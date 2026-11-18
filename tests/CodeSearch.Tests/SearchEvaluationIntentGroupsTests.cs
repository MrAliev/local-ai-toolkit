using CodeSearch.Core.Search;

namespace CodeSearch.Tests;

public sealed class SearchEvaluationIntentGroupsTests
{
    [Fact]
    public void Translations_are_paired_and_partial_success_is_visible()
    {
        var corpus = Corpus();
        var hit = new SearchEvaluationHit("Answer.cs", "Answer", 0, 1, 20, 2);
        SearchEvaluationObservation[] observations = [Observation("p-en", hit), Observation("p-ru"),
            Observation("n-en", hit), Observation("n-ru")];
        var result = SearchEvaluationIntentGroups.Measure(corpus, observations, Groups());
        Assert.Equal(2, result.IntentCount);
        Assert.Equal(0, result.BothSucceeded);
        Assert.Equal(2, result.OneSucceeded);
        Assert.Equal(0, result.NeitherSucceeded);
        Assert.Equal(1, result.PositiveBothSucceeded.Denominator);
        Assert.Equal(0, result.PositiveBothSucceeded.Numerator);
        Assert.Equal(1, result.NoAnswerAnyHit.Denominator);
        Assert.Equal(1, result.NoAnswerAnyHit.Numerator);
    }

    [Fact]
    public void A_missing_observation_cannot_be_counted_as_abstention()
    {
        Assert.Throws<InvalidDataException>(() => SearchEvaluationIntentGroups.Measure(Corpus(),
            [Observation("p-en"), Observation("p-ru"), Observation("n-en")], Groups()));
    }

    [Fact]
    public void Duplicate_members_or_incomplete_groups_do_not_inflate_sample_size()
    {
        var observations = Corpus().Cases.Select(item => Observation(item.Id)).ToArray();
        Assert.Throws<InvalidDataException>(() => SearchEvaluationIntentGroups.Measure(Corpus(), observations,
            [new("positive", ["p-en", "p-ru"]), new("negative", ["p-en", "n-ru"])]));
        Assert.Throws<InvalidDataException>(() => SearchEvaluationIntentGroups.Measure(Corpus(), observations,
            [new("positive", ["p-en", "p-ru"])]));
    }

    [Fact]
    public void Missing_case_kind_has_unknown_rate_and_bounds()
    {
        var corpus = Corpus() with { Cases = Corpus().Cases.Take(2).ToArray() };
        var result = SearchEvaluationIntentGroups.Measure(corpus, [Observation("p-en"), Observation("p-ru")], [Groups()[0]]);
        Assert.Equal(0, result.NoAnswerAnyHit.Denominator);
        Assert.Null(result.NoAnswerAnyHit.Rate);
        Assert.Null(result.NoAnswerAnyHit.Lower);
        Assert.Null(result.NoAnswerAnyHit.Upper);
    }

    [Theory]
    [InlineData(0, 10, 0, 0.2775327998628892)]
    [InlineData(5, 10, 0.236593090512564, 0.763406909487436)]
    [InlineData(10, 10, 0.7224672001371107, 1)]
    public void Wilson_reference_values_include_boundary_samples(int numerator, int denominator, double lower, double upper)
    {
        var result = SearchEvaluationIntentGroups.Wilson95(numerator, denominator);
        Assert.Equal(lower, result.Lower!.Value, 12);
        Assert.Equal(upper, result.Upper!.Value, 12);
    }

    [Fact]
    public void Large_denominators_do_not_overflow_integer_intermediate_products()
    {
        var result = SearchEvaluationIntentGroups.Wilson95(0, 1_000_000);
        Assert.Equal(0, result.Lower!.Value, 12);
        Assert.InRange(result.Upper!.Value, 0.0000038414, 0.0000038415);
    }

    private static SearchEvaluationCorpus Corpus() => new(1,
        [new("p-en", "positive", "en-natural", false, [new("Answer.cs", null)]),
         new("p-ru", "positive", "ru-natural", false, [new("Answer.cs", null)]),
         new("n-en", "absent", "no-answer", true, []), new("n-ru", "absent", "no-answer", true, [])]);
    private static SearchEvaluationIntentGroup[] Groups() => [new("positive", ["p-en", "p-ru"]), new("negative", ["n-en", "n-ru"])];
    private static SearchEvaluationObservation Observation(string id, params SearchEvaluationHit[] hits) => new(id, hits, TimeSpan.Zero, null);
}
