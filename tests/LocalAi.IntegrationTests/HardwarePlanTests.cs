using LocalAi.Contracts;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;

namespace LocalAi.IntegrationTests;

public sealed class HardwarePlanTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static ModelRoutingCatalogDocument Catalog(params int[] contexts) => new(1, "fixture-catalog",
        [new("fixture-model", "fixture", LocalModelLifecycle.Recommended, LocalModelInstallPolicy.Recommended,
            [LocalModelCapability.Text], contexts, false, null)], [], []);
    private static ReleaseManifest Release(params int[] contexts) => new(1, "0.0.0-test", "fixture-version", "fixture-catalog",
        BrokerCompatibilityContract.ProtocolVersion, BrokerCompatibilityContract.BuildCompatibilityId,
        new Uri("https://example.invalid/package.zip"), (long)GiB, new string('A', 64), false,
        contexts.Select(context => new ManifestModel("fixture-model", context, (long)(4 * GiB), (long)(4 * GiB))).ToArray());
    private static GpuSnapshot Gpu(params ulong[] capacities) => new(ObservationState.Available,
        capacities.Select((bytes, index) => new GpuAdapterSnapshot("fixture-" + index, "Fixture GPU", bytes, false)), null);
    private static HardwarePlan Build(ulong[] vram, ulong? ram, ModelResidencyPolicy policy = ModelResidencyPolicy.RequireFullVram) =>
        HardwarePlanBuilder.Build(Release(2048, 8192), Catalog(2048, 8192), Gpu(vram),
            new(ram is null ? ObservationState.Unknown : ObservationState.Available, ram, ram),
            new(ObservationState.Available, (long)(20 * GiB), null), policy, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(4, 16, "exceeds", "exceeds")]
    [InlineData(6, 16, "fits", "exceeds")]
    [InlineData(8, 16, "fits", "fits")]
    public void Context_reserves_change_estimates_without_claiming_residency(int gpuGiB, int ramGiB, string small, string large)
    {
        var plan = Build([(ulong)gpuGiB * GiB], (ulong)ramGiB * GiB);
        Assert.Equal([small, large], plan.Models.Select(model => model.PlacementEstimate));
        Assert.All(plan.Models, model => Assert.Equal("preflight-required", model.ResidencyProof));
        Assert.Equal(4 * GiB + GiB + 2048 * 256UL * 1024, plan.Models[0].RequiredMemoryBytes);
        Assert.False(plan.ChangesPolicy);
    }

    [Fact]
    public void Two_small_gpus_are_not_silently_aggregated_into_a_larger_device()
    {
        var plan = Build([4 * GiB, 4 * GiB], 16 * GiB);
        Assert.All(plan.Models, model => Assert.Equal("exceeds", model.VramEstimate));
        Assert.Equal(4 * GiB, plan.SelectedAdapter?.DedicatedLocalBytes);
    }

    [Fact]
    public void Laptop_without_dedicated_gpu_keeps_strict_policy_and_requires_an_explicit_change()
    {
        var strict = Build([], 16 * GiB);
        Assert.Equal("RequireFullVram", strict.ResidencyPolicy);
        Assert.All(strict.Models, model => Assert.Equal("exceeds", model.PlacementEstimate));
        var explicitCpu = Build([], 16 * GiB, ModelResidencyPolicy.AllowCpu);
        Assert.Equal("AllowCpu", explicitCpu.ResidencyPolicy);
        Assert.All(explicitCpu.Models, model => Assert.Equal("fits", model.PlacementEstimate));
        Assert.All(explicitCpu.Models, model => Assert.Equal("preflight-required", model.ResidencyProof));
        Assert.False(explicitCpu.ChangesPolicy);
    }

    [Fact]
    public void Unknown_ram_cannot_turn_a_cpu_plan_into_a_known_fit()
    {
        var plan = Build([], null, ModelResidencyPolicy.AllowCpu);
        Assert.All(plan.Models, model => Assert.Equal("unknown", model.PlacementEstimate));
    }

    [Fact]
    public void Small_ram_is_visible_even_when_dedicated_gpu_capacity_is_large()
    {
        var plan = Build([16 * GiB], 4 * GiB);
        Assert.All(plan.Models, model => Assert.Equal("fits", model.VramEstimate));
        Assert.All(plan.Models, model => Assert.Equal("exceeds", model.RamEstimate));
        Assert.All(plan.Models, model => Assert.Equal("exceeds", model.PlacementEstimate));
    }

    [Fact]
    public void Signed_downloads_are_counted_once_per_model_and_disk_fit_is_not_invented()
    {
        var plan = Build([16 * GiB], 32 * GiB);
        Assert.Equal(4 * GiB, plan.Disk.KnownModelDownloadBytes);
        Assert.Equal(5 * GiB, plan.Disk.KnownMinimumBytes);
        Assert.False(plan.Disk.Complete);
        Assert.Equal("incomplete", plan.Disk.Assessment);
        var lowDisk = HardwarePlanBuilder.Build(Release(2048), Catalog(2048), Gpu(16 * GiB),
            new(ObservationState.Available, 32 * GiB, 20 * GiB), new(ObservationState.Available, 1, null), ModelResidencyPolicy.RequireFullVram, TestContext.Current.CancellationToken);
        Assert.Equal("exceeds-known-minimum", lowDisk.Disk.Assessment);
    }

    [Fact]
    public void A_configured_context_missing_from_signed_release_metadata_has_unknown_costs()
    {
        var plan = HardwarePlanBuilder.Build(Release(2048), Catalog(8192), Gpu(16 * GiB),
            new(ObservationState.Available, 32 * GiB, 20 * GiB), new(ObservationState.Unknown, null, null), ModelResidencyPolicy.RequireFullVram, TestContext.Current.CancellationToken);
        var option = Assert.Single(plan.Models);
        Assert.Null(option.RequiredMemoryBytes);
        Assert.Null(option.DownloadBytes);
        Assert.Equal("unknown", option.PlacementEstimate);
        Assert.Equal(1, plan.Disk.UnknownModelDownloads);
        Assert.Equal("unknown", plan.Disk.Assessment);
    }

    [Fact]
    public void Disabled_catalog_models_are_excluded_from_the_plan_and_disk_cost()
    {
        var original = Catalog(2048);
        var catalog = original with { Models = [original.Models[0] with { Lifecycle = LocalModelLifecycle.Disabled }] };
        var plan = HardwarePlanBuilder.Build(Release(2048), catalog, Gpu(16 * GiB),
            new(ObservationState.Available, 32 * GiB, 20 * GiB), new(ObservationState.Unknown, null, null), ModelResidencyPolicy.RequireFullVram, TestContext.Current.CancellationToken);
        Assert.Empty(plan.Models);
        Assert.Equal(0UL, plan.Disk.KnownModelDownloadBytes);
    }

    [Fact]
    public void Cancelled_planning_returns_no_profile()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HardwarePlanBuilder.Build(Release(2048), Catalog(2048), Gpu(16 * GiB),
            new(ObservationState.Unknown, null, null), new(ObservationState.Unknown, null, null), ModelResidencyPolicy.RequireFullVram, cancellation.Token));
    }
}
