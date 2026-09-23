using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Models;

public sealed record PhysicalMemorySnapshot(ObservationState State, ulong? TotalBytes, ulong? AvailableBytes);
public sealed record HardwareModelOption(string Model, int ContextTokens, string[] Capabilities, string InstallPolicy,
    bool IncludedInDefaultDiskEstimate, ulong? DownloadBytes, ulong? BaseMemoryBytes, ulong RuntimeReserveBytes,
    ulong ContextReserveBytes, ulong? RequiredMemoryBytes, string VramEstimate, string RamEstimate,
    string PlacementEstimate, string ResidencyProof);
public sealed record HardwareDiskPlan(long? AvailableBytes, ulong KnownModelDownloadBytes, int UnknownModelDownloads,
    ulong PackageBytes, ulong KnownMinimumBytes, bool Complete, string Assessment);
public sealed record HardwarePlan(int SchemaVersion, DateTimeOffset CapturedAtUtc, string ReleaseVersion,
    string CatalogVersion, string ResidencyPolicy, bool ChangesPolicy, string GpuObservation,
    GpuAdapterSnapshot? SelectedAdapter, PhysicalMemorySnapshot Memory, HardwareModelOption[] Models, HardwareDiskPlan Disk);

public static class HardwarePlanBuilder
{
    public static HardwarePlan Build(ReleaseManifest release, ModelRoutingCatalogDocument catalog, GpuSnapshot gpu,
        PhysicalMemorySnapshot memory, DiskSnapshot disk, ModelResidencyPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recommendation = new ModelRecommendationEngine().Recommend(gpu, release.Models);
        var choices = recommendation.Choices.ToDictionary(choice => (choice.Name, choice.ContextTokens));
        var options = new List<HardwareModelOption>();
        foreach (var model in catalog.Models.Where(model => model.Lifecycle != LocalModelLifecycle.Disabled &&
                     model.InstallPolicy != LocalModelInstallPolicy.Never).OrderBy(model => model.Tag, StringComparer.Ordinal))
        {
            foreach (var context in model.ContextTokens.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                choices.TryGetValue((model.Tag, context), out var choice);
                var vram = choice is null ? "unknown" : gpu.State is ObservationState.Available or ObservationState.Unavailable
                    ? choice.IsEnabled ? "fits" : "exceeds" : "unknown";
                var ram = choice is null || memory.State != ObservationState.Available || memory.AvailableBytes is null
                    ? "unknown" : choice.RequiredBytes <= memory.AvailableBytes ? "fits" : "exceeds";
                var placement = choice is null ? "unknown" : policy switch
                {
                    ModelResidencyPolicy.AllowCpu => ram,
                    ModelResidencyPolicy.AllowPartialOffload => Combine(ram, recommendation.SelectedAdapter is not null
                        ? "fits" : gpu.State is ObservationState.Available or ObservationState.Unavailable ? "exceeds" : "unknown"),
                    _ => Combine(vram, ram),
                };
                options.Add(new(model.Tag, context, model.Capabilities.Select(value => value.ToString()).ToArray(),
                    model.InstallPolicy.ToString(), model.InstallPolicy == LocalModelInstallPolicy.Recommended,
                    choice?.SignedDownloadSizeBytes, choice?.SignedBaseEstimateBytes, choice?.RuntimeReserveBytes ?? 0,
                    choice?.ContextReserveBytes ?? 0, choice?.RequiredBytes, vram, ram, placement, "preflight-required"));
            }
        }
        ulong knownDownloads = 0;
        var unknownDownloads = 0;
        foreach (var family in options.Where(option => option.IncludedInDefaultDiskEstimate).GroupBy(option => option.Model, StringComparer.Ordinal))
        {
            var known = family.FirstOrDefault(option => option.DownloadBytes is not null);
            if (known is null) unknownDownloads++;
            else knownDownloads = checked(knownDownloads + known.DownloadBytes!.Value);
        }
        var minimum = checked(knownDownloads + (ulong)release.PackageSize);
        var availableDisk = disk.State == ObservationState.Available && disk.AvailableBytes is >= 0 ? disk.AvailableBytes : null;
        var diskAssessment = availableDisk is null ? "unknown" : (ulong)availableDisk < minimum ? "exceeds-known-minimum" : "incomplete";
        return new(1, DateTimeOffset.UtcNow, release.ReleaseVersion, catalog.CatalogVersion, policy.ToString(), false,
            gpu.State.ToString(), recommendation.SelectedAdapter, memory, options.ToArray(),
            new(availableDisk, knownDownloads, unknownDownloads, (ulong)release.PackageSize, minimum, false, diskAssessment));
    }

    private static string Combine(string left, string right) => left == "exceeds" || right == "exceeds" ? "exceeds"
        : left == "unknown" || right == "unknown" ? "unknown" : "fits";
}
