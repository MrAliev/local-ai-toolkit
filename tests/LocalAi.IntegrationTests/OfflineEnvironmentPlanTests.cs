using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Dependencies;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.IntegrationTests;

public sealed class OfflineEnvironmentPlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Plan_exposes_missing_catalog_components_and_budgets_copies_without_modifying_the_bundle()
    {
        var package = Package();
        using var fixture = new OfflineEnvironmentTests.Fixture(package);
        var built = await Prepare(fixture);
        var before = Directory.GetFiles(fixture.Output, "*", SearchOption.AllDirectories).Order().ToArray();
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, built.ManifestSha256, fixture.Catalog,
            new(ObservationState.Available, long.MaxValue, null), Token);
        Assert.Equal(built.ManifestSha256, plan.ManifestSha256);
        Assert.Equal(DependencyCatalog.Supported.Count, plan.Steps.Count(step => step.Kind == "dependency"));
        Assert.Contains(plan.Steps, step => step.Id == "dependency.git" && step.State == "missing-material");
        var ollama = Assert.Single(plan.Steps, step => step.Id == "dependency.ollama");
        Assert.Equal("recipe-validated", ollama.State); Assert.NotNull(ollama.Dependency);
        Assert.Equal("install-dependency", ollama.Action);
        Assert.Equal(0, Array.FindIndex(plan.Steps, step => step.Kind == "dependency"));
        Assert.True(Array.FindIndex(plan.Steps, step => step.Kind == "runtime") > Array.FindLastIndex(plan.Steps, step => step.Kind == "dependency"));
        Assert.Equal(7, plan.Disk.RuntimeArchiveExpandedBytes);
        Assert.Equal(6, plan.Disk.RuntimeArtifactBytes);
        Assert.Equal(2 * package.Length + 7, plan.Disk.RuntimePreparationBytes);
        Assert.Equal(new FileInfo(Path.Combine(fixture.Output, "ollama.exe")).Length, plan.Disk.DependencyStagingBytes);
        Assert.Equal(6 + Math.Max(plan.Disk.RuntimePreparationBytes, 7 + plan.Disk.DependencyStagingBytes), plan.Disk.KnownConservativeAdditionalBytes);
        Assert.Equal("incomplete", plan.Disk.Assessment); Assert.False(plan.Disk.Complete);
        Assert.Contains("dependency-installed-and-temporary-bytes", plan.Disk.Unknowns);
        Assert.False(plan.ReadyForInstallation); Assert.NotEmpty(plan.Unresolved);
        Assert.Contains("environment.explicit-selection-required", plan.Unresolved);
        Assert.DoesNotContain("environment.execution-not-supported", plan.Unresolved);
        Assert.Equal(before, Directory.GetFiles(fixture.Output, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(built.ManifestSha256, (await fixture.Bundle.VerifyAsync(fixture.Output, built.ManifestSha256, Token)).ManifestSha256);
    }

    [Fact]
    public async Task Disk_budget_includes_the_runtime_stage_retained_while_a_larger_dependency_runs()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package());
        File.WriteAllBytes(Path.Combine(fixture.Source, "ollama.exe"), new byte[10000]);
        var built = await Prepare(fixture);
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, built.ManifestSha256, fixture.Catalog,
            new(ObservationState.Available, long.MaxValue, null), Token);
        Assert.Equal(10013, plan.Disk.KnownConservativeAdditionalBytes);
    }

    [Theory]
    [InlineData(ObservationState.Available, 0, "below-known-budget")]
    [InlineData(ObservationState.Available, -1, "unknown")]
    [InlineData(ObservationState.Failed, 999999, "unknown")]
    public async Task Unknown_or_small_free_space_never_becomes_a_ready_environment(ObservationState state, long available, string assessment)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package()); var built = await Prepare(fixture);
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, built.ManifestSha256, fixture.Catalog, new(state, available, null), Token);
        Assert.Equal(assessment, plan.Disk.Assessment); Assert.False(plan.ReadyForInstallation);
        if (assessment == "unknown") Assert.Null(plan.Disk.AvailableBytes);
    }

    [Fact]
    public async Task Unsupported_recipe_is_reported_without_hiding_other_components_or_running_a_process()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package()); var built = await Prepare(fixture, invalidRecipe: true);
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, built.ManifestSha256, fixture.Catalog, new(ObservationState.Unknown, null, null), Token);
        Assert.Contains(plan.Steps, step => step.Id == "dependency.ollama" && step.State == "invalid-action");
        Assert.Contains(plan.Unresolved, item => item == "dependency.action-invalid:dependency.ollama");
        Assert.Equal(DependencyCatalog.Supported.Count, plan.Steps.Count(step => step.Kind == "dependency"));
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("archive")]
    [InlineData("tampered")]
    public async Task Trusted_plans_require_an_unchanged_bundle_and_a_supported_archive(string damage)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(damage == "archive" ? [1, 2, 3] : Package());
        var built = await Prepare(fixture);
        if (damage == "tampered") File.AppendAllText(Path.Combine(fixture.Output, "ollama.exe"), "changed");
        var failure = await Record.ExceptionAsync(() => Application(fixture).PlanEnvironmentAsync(fixture.Output,
            damage == "pin" ? new string('0', 64) : built.ManifestSha256, fixture.Catalog, new(ObservationState.Unknown, null, null), Token));
        if (damage == "archive")
            Assert.Equal(PackageVerificationProblem.ContentsRejected, Assert.IsType<PackageVerificationException>(failure).Problem);
        else Assert.IsType<InvalidDataException>(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_model_blobs_are_counted_once_and_invalid_graphs_are_explicit(bool invalid)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package());
        var built = await Prepare(fixture, models: true, invalidGraph: invalid);
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, built.ManifestSha256, fixture.Catalog, new(ObservationState.Available, long.MaxValue, null), Token);
        if (invalid)
        {
            Assert.Contains(plan.Unresolved, item => item == "models.graph-invalid");
            Assert.Equal(0, plan.Disk.ModelArtifactBytes); Assert.Contains("model-artifact-bytes", plan.Disk.Unknowns);
        }
        else
        {
            var expected = built.Manifest.Files.Where(file => file.Path.StartsWith("models/", StringComparison.Ordinal)).Sum(file => file.Bytes);
            Assert.Equal(expected, plan.Disk.ModelArtifactBytes);
            Assert.Equal(2, plan.Steps.Count(step => step.Kind == "model" && step.State == "graph-validated"));
        }
        Assert.False(plan.ReadyForInstallation);
    }

    [Fact]
    public async Task Omitted_inventory_rows_cannot_hide_expected_catalog_materials_or_their_unknown_space()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package()); var built = await Prepare(fixture);
        var manifest = built.Manifest with { Components = built.Manifest.Components.Where(component => component.Availability == "included").ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, OfflineEnvironmentBundle.Json);
        File.WriteAllBytes(Path.Combine(fixture.Output, OfflineEnvironmentBundle.ManifestName), bytes);
        var plan = await Application(fixture).PlanEnvironmentAsync(fixture.Output, Convert.ToHexStringLower(SHA256.HashData(bytes)), fixture.Catalog,
            new(ObservationState.Available, long.MaxValue, null), Token);
        Assert.Equal(DependencyCatalog.Supported.Count, plan.Steps.Count(step => step.Kind == "dependency"));
        Assert.Contains("missing-model-artifact-bytes", plan.Disk.Unknowns);
        Assert.Contains("missing-dependency-material-bytes", plan.Disk.Unknowns);
    }

    [Fact]
    public async Task Cli_returns_a_successful_plan_with_explicit_incomplete_readiness_and_a_fresh_disk_observation()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package()); var built = await Prepare(fixture);
        using var output = new StringWriter(); using var error = new StringWriter();
        var observations = 0;
        string[] args = ["--bundle", fixture.Output, "--manifest-sha256", built.ManifestSha256, "--disk", fixture.Source];
        var exit = await OfflinePlanCommand.ExecuteAsync(args, new(output, error), true, Token, fixture.Verifier, fixture.Catalog,
            path => { Assert.Equal(fixture.Source, path); observations++; return new(ObservationState.Available, 0, null); });
        Assert.Equal(0, exit); Assert.Equal(1, observations);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var plan = json.RootElement.GetProperty("data").GetProperty("plan");
        Assert.False(plan.GetProperty("readyForInstallation").GetBoolean());
        Assert.Equal("below-known-budget", plan.GetProperty("disk").GetProperty("assessment").GetString());
    }

    [Theory]
    [InlineData("--disk", @"\\invalid-server\share", 2, "invalid_disk_path")]
    [InlineData("--manifest-sha256", "wrong", 65, "offline_plan_refused")]
    [InlineData("--mode", "apply", 2, "invalid_arguments")]
    public async Task Cli_rejects_unsafe_paths_wrong_pins_and_application_requests(string option, string value, int expected, string code)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(Package()); var built = await Prepare(fixture);
        using var output = new StringWriter(); using var error = new StringWriter();
        var args = new List<string> { "--bundle", fixture.Output, "--manifest-sha256", built.ManifestSha256, "--disk", fixture.Source };
        var index = args.IndexOf(option);
        if (index >= 0) args[index + 1] = value; else args.AddRange([option, value]);
        var exit = await OfflinePlanCommand.ExecuteAsync(args.ToArray(), new(output, error), true, Token, fixture.Verifier, fixture.Catalog);
        Assert.Equal(expected, exit); Assert.Contains(code, output.ToString());
        Assert.DoesNotContain(value == "apply" ? fixture.Output : value, output.ToString());
    }

    private static OfflineBundleApplication Application(OfflineEnvironmentTests.Fixture fixture) => new(fixture.Verifier, new ForbiddenRunner());
    private sealed class ForbiddenRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A plan must never run a process.");
    }

    private static byte[] Package()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var name in LocalAiPackageLayout.PackageArtifactFiles.Append(ReleasePackageVerifier.PackageMetadataFileName))
            {
                using var file = archive.CreateEntry(name, CompressionLevel.NoCompression).Open(); file.WriteByte(1);
            }
        return stream.ToArray();
    }

    private static async Task<OfflineBundleResult> Prepare(OfflineEnvironmentTests.Fixture fixture, bool invalidRecipe = false, bool models = false, bool invalidGraph = false)
    {
        var components = new List<OfflineSourceComponent>
        {
            new("runtime", "localai", "0.0.0-test", ["offline-actions.json"]),
            new("dependency", "dependency.ollama", "fixture", ["ollama.exe"]),
        };
        File.WriteAllText(Path.Combine(fixture.Source, "offline-actions.json"), JsonSerializer.Serialize(new OfflineActionDocument(1,
            [new("dependency.ollama", invalidRecipe ? "script" : "exe", "ollama.exe", [])]), OfflineEnvironmentBundle.Json));
        if (models)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 42 }));
            var blob = "models/blobs/sha256-" + hash;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Source, blob))!);
            File.WriteAllBytes(Path.Combine(fixture.Source, blob), [42]);
            foreach (var model in fixture.Catalog.Models.Where(model => model.Lifecycle != LocalModelLifecycle.Disabled && model.InstallPolicy != LocalModelInstallPolicy.Never).Take(2))
            {
                var parts = model.Tag.Split(':'); var path = "models/manifests/registry.ollama.ai/library/" + parts[0] + "/" + (parts.Length == 2 ? parts[1] : "latest");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Source, path))!);
                File.WriteAllText(Path.Combine(fixture.Source, path), JsonSerializer.Serialize(new { schemaVersion = 2, config = new { digest = "sha256:" + hash, size = 1 },
                    layers = new[] { new { digest = "sha256:" + hash, size = invalidGraph ? 2 : 1 } } }));
                components.Add(new("model", model.Tag, "fixture", [path, blob]));
            }
        }
        File.WriteAllText(Path.Combine(fixture.Source, OfflineEnvironmentBundle.SourceName), JsonSerializer.Serialize(new OfflineSourceDocument(1, components.ToArray()), OfflineEnvironmentBundle.Json));
        return await fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token);
    }
}
