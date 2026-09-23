using System.Text.Json;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Abstractions;

namespace LocalAi.IntegrationTests;

public sealed class OfflineRetainedMaterialBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disk_plan_reserves_published_npm_materials_in_addition_to_temporary_staging()
    {
        using var fixture = new OfflineNpmTests.Fixture(); var built = await fixture.Build();
        var application = new OfflineBundleApplication(fixture.Materials.Verifier, new ForbiddenRunner());
        var plan = await application.PlanEnvironmentAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Materials.Catalog,
            new(ObservationState.Unknown, null, null), Token);
        var retained = plan.Steps.Where(step => step.Npm is not null).Sum(step => step.Npm!.Bytes);
        Assert.True(retained > 0);
        Assert.Equal(BaseBudget(plan.Disk) + retained, plan.Disk.KnownConservativeAdditionalBytes);
        Assert.Contains("npm-installed-and-generated-bytes", plan.Disk.Unknowns);
        Assert.False(plan.Disk.Complete);
        Assert.Equal(retained, JsonSerializer.SerializeToElement(plan.Disk).GetProperty("NpmRetainedMaterialBytes").GetInt64());
    }

    [Fact]
    public async Task Disk_plan_reserves_both_retained_and_installed_uninstaller_copies()
    {
        using var fixture = new OfflineExecutionTests.Fixture(); var built = await fixture.Build(includeUninstaller: true);
        var application = new OfflineBundleApplication(fixture.Materials.Verifier, new ForbiddenRunner());
        var plan = await application.PlanEnvironmentAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Materials.Catalog,
            new(ObservationState.Unknown, null, null), Token);
        var copies = 2 * new FileInfo(Path.Combine(fixture.Materials.Output, "LocalAi.Installer.exe")).Length;
        Assert.Equal(BaseBudget(plan.Disk) + copies, plan.Disk.KnownConservativeAdditionalBytes);
        Assert.Equal(copies, JsonSerializer.SerializeToElement(plan.Disk).GetProperty("UninstallerAdditionalBytes").GetInt64());
    }

    private static long BaseBudget(OfflineEnvironmentDisk disk) => disk.RuntimeArtifactBytes + disk.ModelArtifactBytes +
        Math.Max(disk.RuntimePreparationBytes, disk.RuntimeArchiveExpandedBytes + disk.DependencyStagingBytes);

    private sealed class ForbiddenRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A plan must never run a process.");
    }
}
