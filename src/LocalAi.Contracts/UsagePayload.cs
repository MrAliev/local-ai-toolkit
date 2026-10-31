namespace LocalAi.Contracts;

public sealed record UsagePayload<T>(UsageReport Report, T Value);

/// <summary>
/// Measures the complete surface payload, with an optional model-visible projection for tokens. Render must
/// be pure: create nonces/body once before calling. A hard bound also handles discontinuous
/// tokenizers; an unstable size is unknown, never the size of an earlier payload.
/// </summary>
public static class UsagePayload
{
    /// <summary>Compatibility projection for nullable 32-bit CLI estimate fields. No legacy fallback.</summary>
    public static int? LegacySavedTokensEstimate(UsageReport? report) =>
        report?.ContextEstimate?.SavedTokens is { Status: UsageValueStatus.Estimated or UsageValueStatus.Measured,
            Value: >= int.MinValue and <= int.MaxValue } saved ? (int)saved.Value.Value : null;

    public static UsagePayload<T> Finalize<T>(UsageReport report, Func<UsageReport, T> render, Func<T, string> serialize,
        CloudTokenScenario? scenario = null, ICloudTokenProfile? profile = null, Func<T, string?>? modelVisibleResponse = null)
    {
        report = RuntimeCloudComparison.MarkConditional(CloudTokenMethodology.Apply(report, scenario, null, profile));
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var payload = render(report);
            var serialized = serialize(payload);
            var visible = modelVisibleResponse is null ? serialized : modelVisibleResponse(payload);
            var measured = RuntimeCloudComparison.MarkConditional(
                CloudTokenMethodology.Apply(report, scenario, serialized, profile, visible));
            if (measured.Volumes[UsageVolume.ResponseBytes] == report.Volumes[UsageVolume.ResponseBytes]
                && measured.Volumes[UsageVolume.ResponseCharacters] == report.Volumes[UsageVolume.ResponseCharacters]
                && measured.CloudTokens.All(pair => pair.Value == report.CloudTokens[pair.Key])
                && measured.ContextEstimate == report.ContextEstimate)
                return new(report, payload);
            report = measured;
        }
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.ResponseBytes] = UsageValue.Unknown("response_size_unstable");
        volumes[UsageVolume.ResponseCharacters] = UsageValue.Unknown("response_size_unstable");
        var tokens = report.CloudTokens.ToDictionary();
        tokens[UsageCloudTokens.InputWithDelegation] = UsageValue.Unknown("response_size_unstable");
        tokens[UsageCloudTokens.InputSaved] = UsageValue.Unknown("response_size_unstable");
        report = report with { Volumes = volumes, CloudTokens = tokens,
            ContextEstimate = report.ContextEstimate is { } estimate ? estimate with
            {
                ReturnedTokens = UsageValue.Unknown("response_size_unstable"),
                SavedTokens = UsageValue.Unknown("response_size_unstable")
            } : null };
        return new(report, render(report));
    }
}
