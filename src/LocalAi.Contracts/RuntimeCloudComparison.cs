using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace LocalAi.Contracts;

/// <summary>Bundled BPE vocabulary: no network, provider request or local model is involved.</summary>
public sealed class O200kCloudTokenProfile : ICloudTokenProfile
{
    public static O200kCloudTokenProfile Instance { get; } = new();
    private readonly Lazy<TiktokenTokenizer> tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    // The published o200k_base pre-tokenization expression, without special-token matching.
    // CreateForEncoding installs reserved strings unconditionally; source text must encode
    // them ordinarily, not turn them into protocol control tokens. This path is rarely needed.
    private static readonly Regex OrdinaryPieces = new(
        @"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+(?i:'s|'t|'re|'ve|'m|'ll|'d)?|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*(?i:'s|'t|'re|'ve|'m|'ll|'d)?|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    public string TextProfileId => "microsoft-ml-tokenizers-2.0.0:o200k_base";
    public string VisionProfileId => "openai-gpt-4o-tiles-v1";
    public UsageValue CountText(string text)
    {
        var encoder = tokenizer.Value;
        if (encoder.SpecialTokens?.Keys.Any(token => text.Contains(token, StringComparison.Ordinal)) != true)
            return UsageValue.Measured(encoder.CountTokens(text));
        long count = 0;
        try
        {
            foreach (var piece in OrdinaryPieces.EnumerateMatches(text))
                count += encoder.CountTokens(text.AsSpan(piece.Index, piece.Length), considerPreTokenization: false);
        }
        catch (RegexMatchTimeoutException) { return UsageValue.Unknown("tokenization_timeout"); }
        return UsageValue.Measured(count);
    }
    public UsageValue CountImage(CloudImageSource image)
    {
        // Published input-vision formula, not an assertion about the caller's cloud model:
        // https://developers.openai.com/api/docs/guides/images-vision
        if (image.Width <= 0 || image.Height <= 0) return UsageValue.Unknown("invalid_image_dimensions");
        if (image.Detail == "low") return UsageValue.Estimated(85, VisionProfileId, rangeReason: "published_formula");
        if (image.Detail != "high") return UsageValue.Unknown("image_detail_unknown");
        var scale = Math.Min(1d, 2048d / Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Floor(image.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(image.Height * scale));
        if (Math.Min(width, height) > 768)
        {
            scale = 768d / Math.Min(width, height);
            width = Math.Max(1, (int)Math.Floor(width * scale));
            height = Math.Max(1, (int)Math.Floor(height * scale));
        }
        var tiles = ((width + 511) / 512) * ((height + 511) / 512);
        return UsageValue.Estimated(85 + 170L * tiles, VisionProfileId, rangeReason: "published_formula");
    }
}

/// <summary>
/// Per-call comparison of the explicitly supplied task context with the tool exchange.
/// Incidental reads (hashing, retrieval and indexing) do not establish necessary task context.
/// Cloud history is unknown unless supplied; locally generated text earns no cloud credit.
/// This bounded comparison is not account billing or an estimate of an entire conversation.
/// Only hashes and token counts survive source reads; nothing here is serialized or persisted.
/// </summary>
public sealed class RuntimeCloudComparison
{
    private readonly object gate = new();
    private readonly Dictionary<string, CloudSource> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sourceSnapshots = new(StringComparer.Ordinal);
    private CloudContextPresence presence = CloudContextPresence.Unknown;
    private CloudContextBasis basis = CloudContextBasis.Unknown;
    private string? unknownBaselineReason;
    private bool sourceObserved;
    private string? notApplicableReason;
    private bool evidenceLimitReached;
    public ICloudTokenProfile Profile => O200kCloudTokenProfile.Instance;

    public void SourceContext(CloudContextPresence value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) presence = value;
    }

    public void SourceText(string text, string identity = "input")
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            sourceObserved = true;
            if (unknownBaselineReason is not null || evidenceLimitReached) return;
            var snapshot = Hash(text);
            var identityKey = Hash(identity);
            if (sourceSnapshots.TryGetValue(identityKey, out var previous) && previous != snapshot)
            {
                // Without offsets/snapshot provenance, different slices can overlap. Do not
                // add them as distinct required context or guess which revision is intended.
                unknownBaselineReason = "source_observation_overlap_unknown";
                return;
            }
            var id = Hash(identity + "\0" + snapshot);
            Record(id, new CloudCountedTextSource(id, Profile.CountText(text), Profile.TextProfileId));
            if (!evidenceLimitReached) sourceSnapshots[identityKey] = snapshot;
        }
    }

    public void ContextBasis(CloudContextBasis value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) basis = value;
    }

    public void UnknownBaseline(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate) { sourceObserved = true; unknownBaselineReason = reason; }
    }

    public void SourceImage(string identity, int width, int height, string detail = "high")
    {
        var id = Hash(identity);
        lock (gate)
        {
            sourceObserved = true;
            if (unknownBaselineReason is not null || evidenceLimitReached) return;
            var snapshot = $"image:{width}:{height}:{detail}";
            if (sourceSnapshots.TryGetValue(id, out var previous) && previous != snapshot)
            {
                unknownBaselineReason = "source_observation_overlap_unknown";
                return;
            }
            Record(id, new CloudImageSource(id, width, height, detail));
            if (!evidenceLimitReached) sourceSnapshots[id] = snapshot;
        }
    }

    // Keep observation memory bounded even for an arbitrarily long streamed log. A limit
    // loses the baseline explicitly; it must never turn a partial count into claimed savings.
    private void Record(string id, CloudSource source)
    {
        sourceObserved = true;
        if (evidenceLimitReached) return;
        if (sources.Count >= 16_384 && !sources.ContainsKey(id))
        {
            sources.Clear();
            sourceSnapshots.Clear();
            sources["limit"] = new CloudCountedTextSource("limit", UsageValue.Unknown("source_evidence_limit"), Profile.TextProfileId);
            evidenceLimitReached = true;
            return;
        }
        sources[id] = source;
    }

    public void NoSource() { lock (gate) sourceObserved = true; }
    public void NotApplicable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate) notApplicableReason = reason;
    }
    // Compatibility observation: a local answer is not evidence of what the cloud would
    // have generated. Actual local output remains in model receipts, not avoided tokens.
    public void GeneratedText(string text) { ArgumentNullException.ThrowIfNull(text); }

    public CloudTokenScenario? Scenario(string? request)
    {
        lock (gate)
        {
            if (notApplicableReason is { } reason)
                return new("runtime-not-applicable-v1", [], null, request, request) { NotApplicableReason = reason };
            if (!sourceObserved) return null;
            var context = presence;
            var missingBaseline = unknownBaselineReason
                ?? (context == CloudContextPresence.NotPresent && basis != CloudContextBasis.ExplicitTaskInput
                    && sources.Count > 0 && !evidenceLimitReached ? "request_context_not_declared" : null);
            var snapshot = missingBaseline is { } unknown
                ? [(CloudSource)new CloudCountedTextSource("request-context", UsageValue.Unknown(unknown),
                    Profile.TextProfileId, CloudContextPresence.NotPresent)]
                : sources.Values.Select(source => source switch
            {
                CloudCountedTextSource text => (CloudSource)(text with { Presence = context }),
                CloudImageSource image => image with { Presence = context },
                _ => throw new InvalidOperationException("Unsupported observation."),
            }).ToArray();
            return new("runtime-request-context-v2:" + context + ":" + basis, snapshot,
                string.Empty, request, request);
        }
    }

    public static UsageReport MarkConditional(UsageReport report)
    {
        if (report.Comparison.ScenarioId?.StartsWith("runtime-request-context-v2:", StringComparison.Ordinal) != true) return report;
        // Tokenizer counts are measurements in the named vocabulary. Their counterfactual
        // difference is still an estimate, including when every count is known exactly.
        var tokens = report.CloudTokens.ToDictionary();
        var unusableResult = report.Outcome.Delivery != UsageDelivery.Delivered ? "result_not_delivered"
            : report.Outcome.Technical != UsageTechnicalOutcome.Succeeded
                || report.Outcome.Validation is not (UsageValidation.Passed or UsageValidation.NotApplicable)
                ? "result_not_validated" : null;
        foreach (var key in new[] { UsageCloudTokens.InputSaved, UsageCloudTokens.OutputSaved })
            if (unusableResult is not null) tokens[key] = UsageValue.Unknown(unusableResult);
            else if (tokens[key] is { Status: UsageValueStatus.Measured, Value: { } value })
                tokens[key] = UsageValue.Estimated(value, report.Comparison.ScenarioId, rangeReason: "conditional_scenario");
        return report with { CloudTokens = tokens };
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
