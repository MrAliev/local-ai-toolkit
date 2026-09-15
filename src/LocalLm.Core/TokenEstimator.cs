using LocalLm.Core.Resources;
using LocalAi.Contracts;
using System.Globalization;

namespace LocalLm.Core;

/// <summary>
/// Compatibility counters for existing callers. New reports use CloudTokenMethodology with
/// explicit provenance, the full tool payload and a named cloud tokenization profile.
/// These scalar estimates cannot represent unknown data or measured backend tokens.
/// </summary>
public static class TokenEstimator
{
    public static int ForText(string text) => HeuristicCloudTokenProfile.EstimateText(text);

    /// <summary>
    /// Legacy area heuristic, not a cloud vision profile. New reports return unknown until
    /// a specific vision profile is supplied. Retained for existing scalar callers.
    /// </summary>
    public static int ForImage(ImageInfo image) =>
        (int)Math.Ceiling(image.Width * (double)image.Height / 750.0);

    /// <summary>
    /// A signed difference. The caller owns the comparison scenario and must supply the full
    /// returned payload when available; a longer response can increase cloud input.
    /// </summary>
    public static int Saved(int wouldHaveCost, string actualAnswer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wouldHaveCost);
        return checked((int)CloudTokenMethodology.Difference(
            UsageValue.Estimated(wouldHaveCost, HeuristicCloudTokenProfile.Instance.TextProfileId),
            HeuristicCloudTokenProfile.Instance.CountText(actualAnswer)).Value!.Value);
    }

    /// <summary>
    /// Legacy comparison assuming previously unseen source text and no wrapper overhead.
    /// Generation and input are separate; writing output to a file never counts that output
    /// a second time as saved input. Use CloudTokenMethodology when provenance is unknown.
    /// </summary>
    public static TranslationTokenMetrics ForTranslation(string source, string translated, TranslationDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        var input = ForText(source);
        var output = ForText(translated);
        return new(input, output, checked(input + output), output,
            delivery is TranslationDelivery.ToFile ? input : Saved(input, translated));
    }

    public static string Describe(int saved) => DescribeRange(saved);

    /// <summary>Historical name retained for callers. Returns a point, never an invented band.</summary>
    public static string DescribeRange(long saved)
    {
        if (saved == 0) return "0";
        var magnitude = Math.Abs((decimal)saved);
        var divisor = magnitude >= 1_000_000 ? 1_000_000m : magnitude >= 1000 ? 1000m : 1m;
        var rounded = Round(magnitude / divisor);
        if (divisor == 1000 && rounded >= 1000) { divisor = 1_000_000; rounded = Round(magnitude / divisor); }
        var suffix = divisor == 1_000_000 ? "M" : divisor == 1000 ? "K" : "";
        return "~" + (saved < 0 ? "-" : "") + rounded.ToString("0.#", CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>The sign selects the direction; zero does not establish why the count stayed unchanged.</summary>
    public static string DescribeSaving(int saved) => saved switch
    {
        0 => LocalLmText.SavedNothing,
        < 0 => LocalLmText.Catalogue.Format("CloudTokensIncreased", DescribeRange(-(long)saved)),
        _ => LocalLmText.SavedAbout(Describe(saved)),
    };

    private static decimal Round(decimal value) => Math.Round(value, value < 10 ? 1 : 0, MidpointRounding.AwayFromZero);
}

/// <summary>Requested destination; does not prove source provenance or successful delivery.</summary>
public enum TranslationDelivery
{
    /// <summary>Returned as text into the caller's context.</summary>
    ToCaller,
    /// <summary>Intended for a file; saving can still fail or the file can later be read.</summary>
    ToFile,
}

public sealed record TranslationTokenMetrics(
    int InputTokens,
    int OutputTokens,
    int LocalTokensProcessed,
    int EstimatedCloudGenerationTokensSaved,
    int EstimatedNetCloudContextTokensSaved);
