namespace CodeSearch.Core.Search;

public sealed record SearchEvaluationIntentGroup(string IntentId, IReadOnlyList<string> CaseIds);
public sealed record SearchEvaluationIntentOutcome(string IntentId, bool NoAnswer, int SuccessfulParaphrases);
public sealed record SearchEvaluationBinomialRange(int Numerator, int Denominator, double? Rate, double? Lower, double? Upper);
public sealed record SearchEvaluationIntentMetrics(
    int IntentCount, int BothSucceeded, int OneSucceeded, int NeitherSucceeded,
    SearchEvaluationBinomialRange PositiveBothSucceeded,
    SearchEvaluationBinomialRange NoAnswerAnyHit,
    IReadOnlyList<SearchEvaluationIntentOutcome> Outcomes)
{
    public string RangeBasis => "descriptive_wilson95_independence_reference_only_synthetic_groups_may_be_correlated";
    public string PositiveSuccess => "every_relevant_target_found_in_top10_for_both_paraphrases";
    public string NegativeFailure => "any_hit_returned_for_either_paraphrase_without_an_annotated_direct_implementation_not_hallucination_rate";
}

/// <summary>Paired-intent reporting over the existing evaluator; neither language is an independent trial.</summary>
public static class SearchEvaluationIntentGroups
{
    public static void ValidateGroups(SearchEvaluationCorpus corpus, IReadOnlyList<SearchEvaluationIntentGroup> groups) =>
        _ = ValidatePartition(corpus, groups);

    public static SearchEvaluationIntentMetrics Measure(SearchEvaluationCorpus corpus,
        IReadOnlyList<SearchEvaluationObservation> observations, IReadOnlyList<SearchEvaluationIntentGroup> groups)
    {
        var cases = ValidatePartition(corpus, groups);
        ArgumentNullException.ThrowIfNull(observations);
        var seenObservations = new HashSet<string>(StringComparer.Ordinal);
        var observed = new Dictionary<string, SearchEvaluationObservation>(StringComparer.Ordinal);
        foreach (var observation in observations)
        {
            if (observation is null || !cases.ContainsKey(observation.CaseId) || !seenObservations.Add(observation.CaseId))
                throw new InvalidDataException("Intent metrics require exactly one observation for every case.");
            observed.Add(observation.CaseId, observation);
        }
        if (observed.Count != cases.Count)
            throw new InvalidDataException("Missing observations are not failed or successful measurements.");

        var outcomes = new List<SearchEvaluationIntentOutcome>();
        foreach (var group in groups)
        {
            var members = group.CaseIds.Select(id => cases[id]).ToArray();
            var successes = 0;
            foreach (var item in members)
            {
                var observation = observed[item.Id];
                var measured = SearchEvaluation.Measure(corpus with { Cases = [item] }, [observation]);
                if (item.NoAnswer ? observation.Hits.Count == 0 : measured.RecallAt10 == 1) successes++;
            }
            outcomes.Add(new(group.IntentId, members[0].NoAnswer, successes));
        }
        var positives = outcomes.Where(item => !item.NoAnswer).ToArray();
        var negatives = outcomes.Where(item => item.NoAnswer).ToArray();
        return new(outcomes.Count, outcomes.Count(item => item.SuccessfulParaphrases == 2),
            outcomes.Count(item => item.SuccessfulParaphrases == 1), outcomes.Count(item => item.SuccessfulParaphrases == 0),
            Wilson95(positives.Count(item => item.SuccessfulParaphrases == 2), positives.Length),
            Wilson95(negatives.Count(item => item.SuccessfulParaphrases != 2), negatives.Length), outcomes);
    }

    private static Dictionary<string, SearchEvaluationCase> ValidatePartition(SearchEvaluationCorpus corpus,
        IReadOnlyList<SearchEvaluationIntentGroup> groups)
    {
        SearchEvaluationCorpus.Validate(corpus);
        ArgumentNullException.ThrowIfNull(groups);
        var cases = corpus.Cases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.IntentId) || !ids.Add(group.IntentId)
                || group.CaseIds is null || group.CaseIds.Count != 2)
                throw new InvalidDataException("An intent requires a unique identity and exactly two paraphrases.");
            foreach (var id in group.CaseIds)
                if (id is null || !cases.ContainsKey(id) || !covered.Add(id))
                    throw new InvalidDataException("Intent groups must partition the case corpus without duplicates.");
            if (cases[group.CaseIds[0]].NoAnswer != cases[group.CaseIds[1]].NoAnswer)
                throw new InvalidDataException("Paraphrases of one intent must agree on answerability.");
        }
        if (covered.Count != cases.Count)
            throw new InvalidDataException("Every case must belong to one intent group.");
        return cases;
    }

    // NIST Engineering Statistics Handbook, section 7.2.4.1. These reference ranges
    // do not establish coverage for correlated templates or user repositories.
    public static SearchEvaluationBinomialRange Wilson95(int numerator, int denominator)
    {
        if (denominator < 0 || numerator < 0 || numerator > denominator)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator == 0) return new(0, 0, null, null, null);
        const double z = 1.959963984540054;
        var n = (double)denominator;
        var p = numerator / n;
        var z2 = z * z;
        var divisor = 1 + z2 / n;
        var center = (p + z2 / (2 * n)) / divisor;
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / divisor;
        return new(numerator, denominator, p, Math.Max(0, center - half), Math.Min(1, center + half));
    }
}
