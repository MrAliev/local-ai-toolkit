namespace LocalAi.Contracts;

public sealed record UsagePayload<T>(UsageReport Report, T Value);

/// <summary>
/// Counts the complete surface payload, including the notice and embedded report. Render must
/// be pure: create nonces/body once before calling. A hard bound also handles discontinuous
/// tokenizers; an unstable size is unknown, never the size of an earlier payload.
/// </summary>
public static class UsagePayload
{
    public static UsagePayload<T> Finalize<T>(UsageReport report, Func<UsageReport, T> render, Func<T, string> serialize)
    {
        report = CloudTokenMethodology.Apply(report, null, null, null);
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var payload = render(report);
            var measured = CloudTokenMethodology.Apply(report, null, serialize(payload), null);
            if (measured.Volumes[UsageVolume.ResponseBytes] == report.Volumes[UsageVolume.ResponseBytes]
                && measured.Volumes[UsageVolume.ResponseCharacters] == report.Volumes[UsageVolume.ResponseCharacters])
                return new(report, payload);
            report = measured;
        }
        var volumes = report.Volumes.ToDictionary();
        volumes[UsageVolume.ResponseBytes] = UsageValue.Unknown("response_size_unstable");
        volumes[UsageVolume.ResponseCharacters] = UsageValue.Unknown("response_size_unstable");
        report = report with { Volumes = volumes };
        return new(report, render(report));
    }
}
