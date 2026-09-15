using System.Collections.ObjectModel;
using System.Text;

namespace LocalAi.Contracts;

public enum CloudContextPresence { Unknown, AlreadyPresent, NotPresent }

/// <summary>Id identifies a content snapshot, not merely a mutable file path.</summary>
public abstract record CloudSource(string Id, CloudContextPresence Presence);
public sealed record CloudTextSource(string Id, string Text, int Offset = 0, CloudContextPresence Presence = CloudContextPresence.Unknown)
    : CloudSource(Id, Presence);
public sealed record CloudImageSource(string Id, int Width, int Height, string Detail, CloudContextPresence Presence = CloudContextPresence.Unknown)
    : CloudSource(Id, Presence);

/// <summary>
/// An explicit counterfactual: only the source reading needed for the same result, the complete
/// desired cloud output, and additional cloud input/output incurred by delegation. The latter
/// includes tool-call arguments and follow-up generation where applicable. Null means unknown;
/// an observed empty string means none. The full tool response is supplied separately, once.
/// Source presence refers to the cloud context before the call, never inferred from a path.
/// </summary>
public sealed record CloudTokenScenario(string Id, IReadOnlyList<CloudSource> Sources,
    string? OutputWithoutDelegation, string? AdditionalInputWithDelegation, string? OutputWithDelegation);

/// <summary>
/// A versioned cloud tokenizer/vision profile. A backend's local prompt/eval counts do not
/// implement this contract. Profiles return measured counts only when they actually tokenize;
/// heuristics return estimates, and unsupported content returns unknown with a reason.
/// </summary>
public interface ICloudTokenProfile
{
    string TextProfileId { get; }
    string? VisionProfileId { get; }
    UsageValue CountText(string text);
    UsageValue CountImage(CloudImageSource image);
}

/// <summary>
/// The compatibility text heuristic, now named and versioned. It uses UTF-16 character length
/// divided by 4, weighted toward 2.2 by the share of Cyrillic letters. Other scripts are not
/// calibrated. It is not a vendor tokenizer and supplies no statistical interval or vision cost.
/// </summary>
public sealed class HeuristicCloudTokenProfile : ICloudTokenProfile
{
    public static HeuristicCloudTokenProfile Instance { get; } = new();
    public string TextProfileId => "weighted-characters-v1";
    public string? VisionProfileId => null;
    public UsageValue CountText(string text) => text.Length == 0 ? UsageValue.Measured(0)
        : UsageValue.Estimated(EstimateText(text), TextProfileId);
    public UsageValue CountImage(CloudImageSource image) => UsageValue.Unknown("vision_profile_unknown");

    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var letters = 0;
        var cyrillic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch is >= 'Ѐ' and <= 'ӿ') cyrillic++;
        }
        var share = letters == 0 ? 0 : (double)cyrillic / letters;
        return (int)Math.Ceiling(text.Length / (4.0 * (1 - share) + 2.2 * share));
    }
}

public static class CloudTokenMethodology
{
    public const string Version = "cloud-comparison-v1";

    /// <summary>
    /// Populates cloud comparisons and observed full-response sizes in the application-owned
    /// report. It never changes backend tokens, model evidence or timing. Call only after the
    /// surface has assembled its entire payload; a missing payload is not an empty response.
    /// No grand total is produced, avoiding an extra sum of overlapping context/generation data.
    /// </summary>
    public static UsageReport Apply(UsageReport report, CloudTokenScenario? scenario, string? fullToolResponse, ICloudTokenProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(report);
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.ResponseBytes] = fullToolResponse is null ? UsageValue.Unknown("full_response_unknown")
            : UsageValue.Measured(Encoding.UTF8.GetByteCount(fullToolResponse));
        volumes[UsageVolume.ResponseCharacters] = fullToolResponse is null ? UsageValue.Unknown("full_response_unknown")
            : UsageValue.Measured(fullToolResponse.Length);
        var tokens = Enum.GetValues<UsageCloudTokens>().ToDictionary(key => key, _ => UsageValue.Unknown("comparison_scenario_unknown"));

        if (report.ComparisonKind == UsageComparisonKind.NotApplicable)
            tokens = tokens.ToDictionary(pair => pair.Key, _ => UsageValue.NotApplicable("administrative_operation"));
        else if (scenario is not null && !string.IsNullOrWhiteSpace(scenario.Id))
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.TextProfileId))
                tokens = tokens.ToDictionary(pair => pair.Key, _ => UsageValue.Unknown("text_profile_unknown"));
            else
            {
                tokens[UsageCloudTokens.InputWithoutDelegation] = CountSources(scenario.Sources, profile);
                tokens[UsageCloudTokens.InputWithDelegation] = fullToolResponse is null
                    ? UsageValue.Unknown("full_response_unknown")
                    : Sum([Text(scenario.AdditionalInputWithDelegation, profile), Text(fullToolResponse, profile)]);
                tokens[UsageCloudTokens.OutputWithoutDelegation] = report.Outcome.Delivery != UsageDelivery.Delivered
                    ? UsageValue.Unknown("result_not_delivered")
                    : report.Outcome.Technical != UsageTechnicalOutcome.Succeeded
                        || report.Outcome.Validation is not (UsageValidation.Passed or UsageValidation.NotApplicable)
                        ? UsageValue.Unknown("result_not_validated") : Text(scenario.OutputWithoutDelegation, profile);
                tokens[UsageCloudTokens.OutputWithDelegation] = Text(scenario.OutputWithDelegation, profile);
                tokens[UsageCloudTokens.InputSaved] = Difference(tokens[UsageCloudTokens.InputWithoutDelegation], tokens[UsageCloudTokens.InputWithDelegation]);
                tokens[UsageCloudTokens.OutputSaved] = Difference(tokens[UsageCloudTokens.OutputWithoutDelegation], tokens[UsageCloudTokens.OutputWithDelegation]);
            }
        }
        return report with
        {
            MethodologyVersion = Version,
            Comparison = new(scenario?.Id, profile?.TextProfileId, profile?.VisionProfileId),
            Volumes = new ReadOnlyDictionary<UsageVolume, UsageValue>(volumes),
            CloudTokens = new ReadOnlyDictionary<UsageCloudTokens, UsageValue>(tokens),
        };
    }

    public static UsageValue Difference(UsageValue withoutDelegation, UsageValue withDelegation)
    {
        var before = Count(withoutDelegation);
        var after = Count(withDelegation);
        if (before.Status == UsageValueStatus.Unknown) return before;
        if (after.Status == UsageValueStatus.Unknown) return after;
        // Both sides are nonnegative long counts, so their signed difference cannot overflow.
        var value = before.Value!.Value - after.Value!.Value;
        return before.Status == UsageValueStatus.Measured && after.Status == UsageValueStatus.Measured
            ? UsageValue.Measured(value) : UsageValue.Estimated(value, Version + ":difference");
    }

    private static UsageValue Text(string? text, ICloudTokenProfile profile) => text is null
        ? UsageValue.Unknown("scenario_content_unknown") : Count(profile.CountText(text));

    private static UsageValue Count(UsageValue? value)
    {
        if (value is { Status: UsageValueStatus.Unknown, Value: null } && !string.IsNullOrWhiteSpace(value.Reason)) return value;
        if (value is null || value.Value is null or < 0 || value.Reason is not null) return UsageValue.Unknown("invalid_profile_count");
        return value.Status switch
        {
            UsageValueStatus.Measured => UsageValue.Measured(value.Value.Value),
            UsageValueStatus.Estimated when !string.IsNullOrWhiteSpace(value.Method) => UsageValue.Estimated(value.Value.Value, value.Method),
            _ => UsageValue.Unknown("invalid_profile_count"),
        };
    }

    private static UsageValue Sum(IEnumerable<UsageValue> values)
    {
        long total = 0;
        var estimated = false;
        foreach (var raw in values)
        {
            var value = Count(raw);
            if (value.Status == UsageValueStatus.Unknown) return value;
            try { total = checked(total + value.Value!.Value); }
            catch (OverflowException) { return UsageValue.Unknown("token_count_overflow"); }
            estimated |= value.Status == UsageValueStatus.Estimated;
        }
        return estimated ? UsageValue.Estimated(total, Version + ":sum") : UsageValue.Measured(total);
    }

    private static UsageValue CountSources(IReadOnlyList<CloudSource>? sources, ICloudTokenProfile profile)
    {
        if (sources is null) return UsageValue.Unknown("source_reading_unknown");
        if (sources.Any(source => source is null || string.IsNullOrWhiteSpace(source.Id) || !Enum.IsDefined(source.Presence)))
            return UsageValue.Unknown("invalid_source_identity");
        var counts = new List<UsageValue>();
        foreach (var group in sources.GroupBy(source => source.Id, StringComparer.Ordinal))
        {
            if (group.All(source => source is CloudTextSource)) counts.Add(CountTextSources(group.Cast<CloudTextSource>().ToArray(), profile));
            else if (group.All(source => source is CloudImageSource))
            {
                var images = group.Cast<CloudImageSource>().ToArray();
                var first = images[0];
                if (first.Width <= 0 || first.Height <= 0 || string.IsNullOrWhiteSpace(first.Detail)
                    || images.Any(image => image.Width != first.Width || image.Height != first.Height || image.Detail != first.Detail))
                    return UsageValue.Unknown("source_content_conflict");
                if (images.Any(image => image.Presence == CloudContextPresence.AlreadyPresent)) continue;
                if (images.Any(image => image.Presence == CloudContextPresence.Unknown)) return UsageValue.Unknown("source_context_unknown");
                counts.Add(string.IsNullOrWhiteSpace(profile.VisionProfileId) ? UsageValue.Unknown("vision_profile_unknown") : Count(profile.CountImage(first)));
            }
            else return UsageValue.Unknown("source_content_conflict");
        }
        return Sum(counts);
    }

    private static UsageValue CountTextSources(CloudTextSource[] sources, ICloudTokenProfile profile)
    {
        if (sources.Any(source => source.Text is null || source.Offset < 0 || source.Offset > int.MaxValue - source.Text.Length))
            return UsageValue.Unknown("source_range_invalid");
        var boundaries = sources.SelectMany(source => new[] { source.Offset, source.Offset + source.Text.Length }).Distinct().Order().ToArray();
        var counts = new List<UsageValue>();
        var pending = new StringBuilder();
        void Flush()
        {
            if (pending.Length == 0) return;
            counts.Add(Text(pending.ToString(), profile));
            pending.Clear();
        }
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            var covering = sources.Where(source => source.Offset <= start && source.Offset + source.Text.Length >= end).ToArray();
            if (covering.Length == 0) { Flush(); continue; }
            var text = covering[0].Text.AsSpan(start - covering[0].Offset, end - start);
            if (covering.Skip(1).Any(source => !source.Text.AsSpan(start - source.Offset, end - start)
                .SequenceEqual(covering[0].Text.AsSpan(start - covering[0].Offset, end - start)))) return UsageValue.Unknown("source_content_conflict");
            if (covering.Any(source => source.Presence == CloudContextPresence.AlreadyPresent)) { Flush(); continue; }
            if (covering.Any(source => source.Presence == CloudContextPresence.Unknown)) return UsageValue.Unknown("source_context_unknown");
            pending.Append(text);
        }
        Flush();
        return Sum(counts);
    }
}
