using System.Text.Json;
using System.IO.Compression;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.IntegrationTests;

public sealed class OfflineNpmTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Environment_plan_recognizes_npm_materials_and_keeps_toolchain_and_space_requirements_explicit()
    {
        using var fixture = new Fixture(); var built = await fixture.Build();
        var application = new OfflineBundleApplication(fixture.Materials.Verifier, new Runner((_, _, _) => throw new InvalidOperationException()));
        var plan = await application.PlanEnvironmentAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Materials.Catalog,
            new(LocalAi.Installer.Core.Diagnosis.ObservationState.Unknown, null, null), Token);
        var step = Assert.Single(plan.Steps, step => step.Id == fixture.Component);
        Assert.Equal("install-npm", step.Action); Assert.Equal("lockfile-validated", step.State);
        Assert.Contains("dependency.npm-toolchain-and-prefix-required:" + fixture.Component, plan.Unresolved);
        Assert.True(plan.Disk.DependencyStagingBytes > 0); Assert.False(plan.ReadyForInstallation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_defaults_to_plan_and_keeps_failure_details_when_apply_is_requested(bool apply)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        using var output = new StringWriter(); using var error = new StringWriter();
        var runner = new Runner((_, _, _) => new(1, "", "", false, false));
        string[] args = ["--bundle", fixture.Materials.Output, "--manifest-sha256", built.ManifestSha256, "--component", fixture.Component,
            "--node", fixture.Node, "--npm-cli", fixture.NpmCli, "--out", fixture.Output, .. apply ? new[] { "--mode", "apply" } : Array.Empty<string>()];
        var exit = await OfflineNpmCommand.ExecuteAsync(args, new(output, error), true, Token, fixture.Materials.Verifier, runner);
        Assert.Equal(apply ? 70 : 0, exit); Assert.Equal(apply ? 1 : 0, runner.Calls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(!apply, json.RootElement.GetProperty("ok").GetBoolean());
        if (apply)
        {
            var result = json.RootElement.GetProperty("data").GetProperty("result");
            Assert.Equal("failed", result.GetProperty("state").GetString());
            Assert.False(result.GetProperty("readyForUse").GetBoolean());
            Assert.NotNull(result.GetProperty("retainedDirectory").GetString());
        }
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public async Task Plan_preserves_inputs_and_apply_uses_isolated_offline_npm_before_publishing_a_new_prefix()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((executable, arguments, directory) =>
        {
            Assert.Equal(fixture.Node, executable); Assert.Equal(fixture.NpmCli, arguments[0]);
            Assert.Contains("--offline", arguments); Assert.Contains("--ignore-scripts", arguments);
            Assert.Contains("--global=false", arguments); Assert.Contains("--no-audit", arguments); Assert.Contains("--no-fund", arguments);
            Assert.NotEqual(fixture.Output, directory); Assert.False(Directory.Exists(fixture.Output));
            Assert.True(File.Exists(Path.Combine(directory, "package.json"))); Assert.True(File.Exists(Path.Combine(directory, "package-lock.json")));
            Assert.Contains("--cache=" + Path.Combine(directory, "cache"), arguments);
            var installed = Path.Combine(directory, "node_modules", "@sourcegraph", "scip-typescript");
            Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "package.json"), "{\"name\":\"@sourcegraph/scip-typescript\",\"version\":\"0.4.0\"}");
            return new(0, "", "", false, false);
        });
        var application = new OfflineBundleApplication(fixture.Materials.Verifier, runner);
        var plan = await application.PlanNpmAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Component, fixture.Node, fixture.NpmCli, fixture.Output, Token);
        Assert.Equal("0.4.0", plan.Version); Assert.Equal(0, runner.Calls); Assert.False(Directory.Exists(fixture.Output));
        var result = await application.ApplyNpmAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Component, fixture.Node, fixture.NpmCli, fixture.Output, Token);
        Assert.Equal("applied", result.State); Assert.False(result.ReadyForUse); Assert.Equal(1, runner.Calls);
        Assert.True(File.Exists(Path.Combine(fixture.Output, "node_modules", "@sourcegraph", "scip-typescript", "package.json")));
        Assert.Equal(built.ManifestSha256, (await fixture.Materials.Bundle.VerifyAsync(fixture.Materials.Output, built.ManifestSha256, Token)).ManifestSha256);
    }

    [Theory]
    [InlineData("wrong-version")]
    [InlineData("extra-dependency")]
    [InlineData("lock-mismatch")]
    [InlineData("remote-link")]
    public async Task Invalid_or_unpinned_project_materials_are_refused_before_a_process_or_output(string damage)
    {
        using var fixture = new Fixture(); var built = await fixture.Build(damage);
        var runner = new Runner((_, _, _) => throw new InvalidOperationException("Must not launch."));
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Materials.Verifier, runner)
            .PlanNpmAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Component, fixture.Node, fixture.NpmCli, fixture.Output, Token));
        Assert.Equal(0, runner.Calls); Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData(1, false, false, "failed")]
    [InlineData(null, true, false, "timed-out")]
    [InlineData(null, false, true, "cancelled")]
    public async Task Failed_runs_do_not_publish_a_prefix(int? exit, bool timeout, bool cancelled, string state)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((_, _, _) => new(exit, "", "", timeout, cancelled));
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, runner).ApplyNpmAsync(
            fixture.Materials.Output, built.ManifestSha256, fixture.Component, fixture.Node, fixture.NpmCli, fixture.Output, Token);
        Assert.Equal(state, result.State); Assert.False(Directory.Exists(fixture.Output)); Assert.False(result.ReadyForUse);
    }

    [Fact]
    public async Task Installer_success_with_a_wrong_installed_version_is_not_published()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((_, _, directory) =>
        {
            var installed = Path.Combine(directory, "node_modules", "@sourcegraph", "scip-typescript");
            Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "package.json"), "{\"version\":\"9.9.9\"}");
            return new(0, "", "", false, false);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Materials.Verifier, runner).ApplyNpmAsync(
            fixture.Materials.Output, built.ManifestSha256, fixture.Component, fixture.Node, fixture.NpmCli, fixture.Output, Token));
        Assert.False(Directory.Exists(fixture.Output));
    }

    private sealed class Runner(Func<string, IReadOnlyList<string>, string, ProcessResult> callback) : IProcessRunner, IProcessWorkingDirectoryRunner
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("An isolated working directory is required.");
        public Task<ProcessResult> RunInDirectoryAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(callback(executable, arguments, workingDirectory)); }
    }

    internal sealed class Fixture : IDisposable
    {
        public OfflineEnvironmentTests.Fixture Materials { get; } = new(Package());
        public string Component => "dependency.scip-typescript";
        public string Output => Path.Combine(Path.GetDirectoryName(Materials.Source)!, "prefix");
        public string Node => Path.Combine(Path.GetDirectoryName(Materials.Source)!, "node.exe");
        public string NpmCli => Path.Combine(Path.GetDirectoryName(Materials.Source)!, "npm-cli.js");
        private static byte[] Package()
        {
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                foreach (var file in LocalAiPackageLayout.PackageArtifactFiles.Append(ReleasePackageVerifier.PackageMetadataFileName))
                { using var entry = archive.CreateEntry(file, CompressionLevel.NoCompression).Open(); entry.WriteByte(1); }
            return bytes.ToArray();
        }
        public async Task<OfflineBundleResult> Build(string? damage = null, bool includeExecutables = false)
        {
            File.WriteAllText(Node, "fixture"); File.WriteAllText(NpmCli, "fixture");
            var prefix = "npm/" + Component + "/";
            var dependencies = new Dictionary<string, string> { ["@sourcegraph/scip-typescript"] = damage == "wrong-version" ? "^0.4.0" : "0.4.0" };
            if (damage == "extra-dependency") dependencies["unexpected"] = "1.0.0";
            var project = new { name = "offline-fixture", version = "1.0.0", @private = true, dependencies };
            var packages = new Dictionary<string, object> { [""] = new { dependencies }, ["node_modules/@sourcegraph/scip-typescript"] = new
                { version = damage == "lock-mismatch" ? "9.9.9" : "0.4.0", resolved = damage == "remote-link" ? "git+https://example.invalid/x" : "https://registry.npmjs.org/@sourcegraph/scip-typescript/-/scip-typescript-0.4.0.tgz", integrity = "sha512-" + Convert.ToBase64String(new byte[64]) } };
            string[] paths = [prefix + "package.json", prefix + "package-lock.json", prefix + "cache/_cacache/content-v2/sha512/aa/bb/fixture"];
            foreach (var path in paths) Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Materials.Source, path))!);
            File.WriteAllText(Path.Combine(Materials.Source, paths[0]), JsonSerializer.Serialize(project));
            File.WriteAllText(Path.Combine(Materials.Source, paths[1]), JsonSerializer.Serialize(new { lockfileVersion = 3, packages }));
            File.WriteAllText(Path.Combine(Materials.Source, paths[2]), "fixture cache entry");
            var components = new List<OfflineSourceComponent> { new("dependency", Component, "0.4.0", paths) };
            if (includeExecutables)
            {
                File.WriteAllText(Path.Combine(Materials.Source, "python.exe"), "fixture");
                File.WriteAllText(Path.Combine(Materials.Source, "offline-actions.json"), JsonSerializer.Serialize(new OfflineActionDocument(1,
                    [new("dependency.ollama", "exe", "ollama.exe", []), new("dependency.python-3-12", "exe", "python.exe", [])]), OfflineEnvironmentBundle.Json));
                components.Add(new("runtime", "localai", "0.0.0-test", ["offline-actions.json"]));
                components.Add(new("dependency", "dependency.ollama", "fixture", ["ollama.exe"]));
                components.Add(new("dependency", "dependency.python-3-12", "fixture", ["python.exe"]));
            }
            File.WriteAllText(Path.Combine(Materials.Source, OfflineEnvironmentBundle.SourceName), JsonSerializer.Serialize(new OfflineSourceDocument(1,
                components.ToArray()), OfflineEnvironmentBundle.Json));
            return await Materials.Bundle.BuildAsync(Materials.Source, Materials.Output, Materials.Catalog, Token);
        }
        public void Dispose() => Materials.Dispose();
    }
}
