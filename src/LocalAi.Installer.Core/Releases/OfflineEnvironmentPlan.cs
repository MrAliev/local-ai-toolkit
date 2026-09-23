using System.Security.Cryptography;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;

namespace LocalAi.Installer.Core.Releases;

public sealed record OfflineEnvironmentStep(string Kind, string Id, string Availability, string Action, string State,
    OfflineDependencyPlan? Dependency = null, OfflineNpmMaterials? Npm = null);
public sealed record OfflineEnvironmentDisk(long RuntimeArchiveExpandedBytes, long RuntimeArtifactBytes, long ModelArtifactBytes,
    long DependencyStagingBytes, long RuntimePreparationBytes, long KnownConservativeAdditionalBytes,
    long? AvailableBytes, string Assessment, bool Complete, string[] Unknowns);
public sealed record OfflineEnvironmentPlan(int SchemaVersion, string ManifestSha256, string ReleaseVersion, string CatalogVersion,
    OfflineEnvironmentStep[] Steps, OfflineEnvironmentDisk Disk, string[] Unresolved, bool ReadyForInstallation);

public sealed partial class OfflineBundleApplication
{
    public static DiskSnapshot ObservePlanDisk(string path)
    {
        // Root-drive observations are valid only for ordinary paths, not mounted-volume
        // or redirected directory aliases whose storage may belong to another device.
        OfflineEnvironmentBundle.EnsureNoLinks(path);
        if (!Directory.Exists(path)) throw Invalid();
        return new SystemDiskProbe().Observe(path);
    }

    public async Task<OfflineEnvironmentPlan> PlanEnvironmentAsync(string bundle, string pin, ModelRoutingCatalogDocument catalog,
        DiskSnapshot disk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = await new OfflineEnvironmentBundle(verifier).VerifyAsync(bundle, pin, token);
        ValidateComponents(snapshot.Manifest);
        var components = snapshot.Manifest.Components.ToDictionary(item => (item.Kind, item.Id));
        var files = snapshot.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var archive = files["localai-package.zip"];
        var packagePath = OfflineEnvironmentBundle.Resolve(bundle, archive.Path);
        OfflineEnvironmentBundle.EnsureNoLinks(packagePath);
        (long ExpandedBytes, long ArtifactBytes) runtime;
        // Hash and inspect the same open file: a changed archive cannot supply a new budget
        // after the bundle verification. No package is extracted or executed during planning.
        await using (var input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
        {
            if (input.Length != archive.Bytes || !Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token))
                    .Equals(archive.Sha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            runtime = ReleasePackageVerifier.InspectArchiveSpace(input);
        }
        var steps = new List<OfflineEnvironmentStep>();
        var unresolved = new List<string>();
        var unknowns = new List<string>
        {
            "dependency-installed-and-temporary-bytes", "existing-version-and-launcher-backups",
            "indexes-and-runtime-caches", "filesystem-overhead-and-free-space-changes", "other-volume-requirements",
        };
        var expected = new HashSet<(string, string)> { ("runtime", "localai") };
        long dependencyStaging = 0;
        foreach (var dependency in DependencyCatalog.Supported)
        {
            token.ThrowIfCancellationRequested();
            expected.Add(("dependency", dependency.ActionId));
            if (!components.TryGetValue(("dependency", dependency.ActionId), out var component) || component.Availability != "included")
            {
                steps.Add(new("dependency", dependency.ActionId, "missing", "supply-material", "missing-material"));
                unresolved.Add("dependency.missing:" + dependency.ActionId); continue;
            }
            try
            {
                if (dependency.InstallerKind == DependencyInstallerKind.Npm)
                {
                    var npm = await NpmMaterialsAsync(bundle, snapshot, component.Id, token);
                    dependencyStaging = Math.Max(dependencyStaging, npm.Bytes);
                    steps.Add(new("dependency", component.Id, "included", "install-npm", "lockfile-validated", Npm: npm));
                    unresolved.Add("dependency.npm-toolchain-and-prefix-required:" + component.Id);
                    continue;
                }
                var action = await DependencyPlanAsync(bundle, snapshot, component.Id, token);
                dependencyStaging = Math.Max(dependencyStaging, action.Files.Sum(path => files[path].Bytes));
                steps.Add(new("dependency", component.Id, "included", "install-dependency", "recipe-validated", action));
            }
            catch (InvalidDataException)
            {
                steps.Add(new("dependency", component.Id, "included", "supply-supported-action", "invalid-action"));
                unresolved.Add("dependency.action-invalid:" + component.Id);
            }
        }
        steps.Add(new("runtime", "localai", "included", "normal-installer", "package-content-verification-required"));
        unresolved.Add("runtime.full-package-validation-and-activation-required");
        var validModels = new HashSet<string>(StringComparer.Ordinal);
        long modelBytes = 0;
        if (components.Values.Any(component => component.Kind == "model" && component.Availability == "included"))
        {
            try
            {
                var artifacts = await ReadModelArtifactsAsync(bundle, snapshot, catalog, token);
                validModels.UnionWith(artifacts.Models); modelBytes = artifacts.Bytes;
            }
            catch (InvalidDataException)
            { unresolved.Add("models.graph-invalid"); unknowns.Add("model-artifact-bytes"); }
        }
        foreach (var model in catalog.Models.Where(model => model.Lifecycle != LocalModelLifecycle.Disabled && model.InstallPolicy != LocalModelInstallPolicy.Never)
                     .OrderBy(model => model.Tag, StringComparer.Ordinal))
        {
            expected.Add(("model", model.Tag));
            var included = components.TryGetValue(("model", model.Tag), out var component) && component.Availability == "included";
            steps.Add(new("model", model.Tag, included ? "included" : "missing", included ? "import-models" : "supply-material",
                !included ? "missing-material" : validModels.Contains(model.Tag) ? "graph-validated" : "invalid-model-graph"));
            if (!included) unresolved.Add("model.missing:" + model.Tag);
        }
        foreach (var component in components.Values.Where(component => !expected.Contains((component.Kind, component.Id))))
        {
            steps.Add(new(component.Kind, component.Id, component.Availability, "resolve-catalog", "unsupported"));
            unresolved.Add("catalog.unsupported-component:" + component.Id);
        }
        if (snapshot.Manifest.CatalogVersion != catalog.CatalogVersion) unresolved.Add("catalog.version-changed");
        if (steps.Any(step => step.Kind == "model" && step.Availability == "missing")) unknowns.Add("missing-model-artifact-bytes");
        if (steps.Any(step => step.Kind == "dependency" && step.Availability == "missing")) unknowns.Add("missing-dependency-material-bytes");
        steps.Add(new("configuration", "ollama-model-root", "not-assessed", "configure-model-root", "manual-step-required"));
        steps.Add(new("validation", "broker-preflight", "not-assessed", "broker-preflight", "target-machine-check-required"));
        unresolved.AddRange(["dependencies.installed-versions-unverified", "models.root-configuration-and-preflight-required",
            "environment.explicit-selection-required", "environment.offline-acceptance-required"]);
        // This is an explicit single-volume, sequential-operation planning reserve, not an
        // installed-size claim. Runtime preparation budgets two compressed copies (directory
        // release feed + verifier) and its unpacked stage. The executor retains the verified
        // runtime stage during dependency execution; model import renames its stage.
        var runtimePreparation = checked(2 * archive.Bytes + runtime.ExpandedBytes);
        var knownBudget = checked(runtime.ArtifactBytes + modelBytes + Math.Max(runtimePreparation, checked(runtime.ExpandedBytes + dependencyStaging)));
        var available = disk.State == ObservationState.Available && disk.AvailableBytes is >= 0 ? disk.AvailableBytes : null;
        var assessment = available is null ? "unknown" : available < knownBudget ? "below-known-budget" : "incomplete";
        return new(1, snapshot.ManifestSha256, snapshot.Manifest.ReleaseVersion, catalog.CatalogVersion, steps.ToArray(),
            new(runtime.ExpandedBytes, runtime.ArtifactBytes, modelBytes, dependencyStaging, runtimePreparation, knownBudget,
                available, assessment, false, unknowns.ToArray()), unresolved.ToArray(), false);
    }
}
