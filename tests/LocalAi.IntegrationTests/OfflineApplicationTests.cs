using System.Security.Cryptography;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;

namespace LocalAi.IntegrationTests;

public sealed class OfflineApplicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_defaults_to_preview_and_keeps_failed_installer_details_in_a_failed_envelope()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture);
        var runner = new Runner((_, _) => new(42, "", "", false, false));
        string[] args = ["install-dependency", "--bundle", fixture.Output, "--manifest-sha256", built.ManifestSha256, "--component", "dependency.ollama"];
        using var output = new StringWriter(); using var error = new StringWriter();
        var preview = await OfflineApplicationCommand.ExecuteAsync(args, new(output, error), true, Token, fixture.Verifier, runner);
        Assert.Equal(0, preview); Assert.Equal(0, runner.Calls);
        output.GetStringBuilder().Clear();
        var applied = await OfflineApplicationCommand.ExecuteAsync([.. args, "--mode", "apply"], new(output, error), true, Token, fixture.Verifier, runner);
        Assert.Equal(70, applied); Assert.Equal(1, runner.Calls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(42, json.RootElement.GetProperty("data").GetProperty("result").GetProperty("exitCode").GetInt32());
        Assert.Equal("offline_install_failed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dependency_preview_does_not_run_and_apply_uses_locked_verified_private_copy()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture();
        var built = await Dependency(fixture);
        string? executed = null;
        var runner = new Runner((executable, arguments) =>
        {
            executed = executable;
            Assert.False(executable.StartsWith(fixture.Output, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("fixture, never execute", File.ReadAllText(executable));
            Assert.Equal(["/quiet", "/norestart"], arguments);
            Assert.Throws<IOException>(() => File.WriteAllText(executable, "replace"));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(executable)!, fixture.Moved));
            return new(0, "", "", false, false);
        });
        var application = new OfflineBundleApplication(fixture.Verifier, runner);
        var preview = await application.PlanDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token);
        Assert.Equal("exe", preview.Kind); Assert.Equal("ollama.exe", preview.Installer); Assert.Equal(0, runner.Calls);
        var result = await application.ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token);
        Assert.Equal("completed", result.State); Assert.Equal(1, runner.Calls); Assert.NotNull(executed);
        Assert.False(File.Exists(executed));
    }

    [Theory]
    [InlineData(1641, "completed", true)]
    [InlineData(3010, "completed", true)]
    [InlineData(42, "failed", false)]
    public async Task Dependency_exit_status_is_preserved_without_claiming_compatibility(int exit, string state, bool reboot)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture);
        var runner = new Runner((_, _) => new(exit, "", "", false, false));
        var result = await new OfflineBundleApplication(fixture.Verifier, runner).ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token);
        Assert.Equal(state, result.State); Assert.Equal(exit, result.ExitCode); Assert.Equal(reboot, result.RebootRequired);
    }

    [Theory]
    [InlineData("undeclared")]
    [InlineData("script")]
    [InlineData("unknown-component")]
    [InlineData("bad-msi-switch")]
    public async Task Unsupported_or_unlisted_install_actions_never_reach_process_execution(string damage)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture();
        var built = await Dependency(fixture, damage);
        var runner = new Runner((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Verifier, runner)
            .ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Tampered_bundle_never_reaches_an_installer()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture);
        File.AppendAllText(Path.Combine(fixture.Output, "ollama.exe"), "tampered");
        var runner = new Runner((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Verifier, runner)
            .ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Model_import_is_previewed_then_published_to_a_new_store_without_running_models()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Models(fixture);
        var runner = new Runner((_, _) => throw new InvalidOperationException());
        var application = new OfflineBundleApplication(fixture.Verifier, runner);
        var plan = await application.ImportModelsAsync(fixture.Output, built.ManifestSha256, fixture.Moved, fixture.Catalog, false, Token);
        Assert.Single(plan.Models); Assert.Equal(3, plan.Files.Length); Assert.True(plan.Bytes > 0); Assert.False(Directory.Exists(fixture.Moved));
        await application.ImportModelsAsync(fixture.Output, built.ManifestSha256, fixture.Moved, fixture.Catalog, true, Token);
        foreach (var path in plan.Files) Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Output, "models", path)), File.ReadAllBytes(Path.Combine(fixture.Moved, path)));
        Assert.Equal("preflight-required", plan.ResidencyProof); Assert.Equal(0, runner.Calls);
        await Assert.ThrowsAsync<IOException>(() => application.ImportModelsAsync(fixture.Output, built.ManifestSha256, fixture.Moved, fixture.Catalog, true, Token));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("extra-proof")]
    [InlineData("missing-blob")]
    public async Task Trusted_inventory_cannot_make_an_invalid_model_graph_importable(string damage)
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Models(fixture, damage);
        var application = new OfflineBundleApplication(fixture.Verifier, new Runner((_, _) => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<InvalidDataException>(() => application.ImportModelsAsync(fixture.Output, built.ManifestSha256, fixture.Moved, fixture.Catalog, true, Token));
        Assert.False(Directory.Exists(fixture.Moved));
    }

    [Fact]
    public async Task Cancellation_leaves_model_destination_unpublished_and_does_not_run_installers()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Models(fixture);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var application = new OfflineBundleApplication(fixture.Verifier, new Runner((_, _) => throw new InvalidOperationException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => application.ImportModelsAsync(fixture.Output, built.ManifestSha256, fixture.Moved, fixture.Catalog, true, cancel.Token));
        Assert.False(Directory.Exists(fixture.Moved));
    }

    [Theory]
    [InlineData(true, false, "timed-out", 75)]
    [InlineData(false, true, "cancelled", 130)]
    public async Task Interrupted_installers_keep_their_state_and_remove_staging_after_confirmed_exit(bool timedOut, bool cancelled, string state, int expectedExit)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture);
        string? staged = null;
        var runner = new Runner((executable, _) => { staged = executable; return new(null, "", "", timedOut, cancelled); });
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await OfflineApplicationCommand.ExecuteAsync(["install-dependency", "--bundle", fixture.Output,
            "--manifest-sha256", built.ManifestSha256, "--component", "dependency.ollama", "--mode", "apply"],
            new(output, error), true, Token, fixture.Verifier, runner);
        Assert.Equal(expectedExit, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(state, json.RootElement.GetProperty("data").GetProperty("result").GetProperty("state").GetString());
        Assert.NotNull(staged); Assert.False(Directory.Exists(Path.GetDirectoryName(staged)));
    }

    [Fact]
    public async Task Unconfirmed_termination_preserves_the_private_files_without_claiming_completion()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture);
        var runner = new Runner((_, _) => throw new ProcessTerminationException(1, ProcessTerminationCause.Timeout, "fixture"));
        var result = await new OfflineBundleApplication(fixture.Verifier, runner).ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token);
        Assert.Equal("termination-unconfirmed", result.State); Assert.Null(result.ExitCode); Assert.False(result.RebootRequired);
        Assert.NotNull(result.RetainedDirectory);
        var retained = Path.GetFullPath(result.RetainedDirectory);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "localai-offline-"), retained, StringComparison.OrdinalIgnoreCase);
        try { Assert.Equal("fixture, never execute", File.ReadAllText(Path.Combine(retained, "ollama.exe"))); }
        finally { Directory.Delete(retained, recursive: true); }
    }

    [Fact]
    public async Task Msi_uses_system_installer_with_separate_arguments_and_a_locked_package()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows staging leases are required.");
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Dependency(fixture, "valid-msi");
        var runner = new Runner((executable, arguments) =>
        {
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), executable);
            Assert.Equal(["/i", arguments[1], "/qn", "/norestart", "INSTALLDIR=C:\\Offline Models"], arguments);
            Assert.Equal("fixture", File.ReadAllText(arguments[1]));
            Assert.Throws<IOException>(() => File.WriteAllText(arguments[1], "changed"));
            return new(0, "", "", false, false);
        });
        var result = await new OfflineBundleApplication(fixture.Verifier, runner).ApplyDependencyAsync(fixture.Output, built.ManifestSha256, "dependency.ollama", Token);
        Assert.Equal("completed", result.State); Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Cli_model_import_defaults_to_plan_and_does_not_replace_an_existing_store()
    {
        using var fixture = new OfflineEnvironmentTests.Fixture(); var built = await Models(fixture);
        using var output = new StringWriter(); using var error = new StringWriter();
        string[] args = ["import-models", "--bundle", fixture.Output, "--manifest-sha256", built.ManifestSha256, "--out", fixture.Moved];
        Assert.Equal(0, await OfflineApplicationCommand.ExecuteAsync(args, new(output, error), true, Token, fixture.Verifier, catalog: fixture.Catalog));
        Assert.False(Directory.Exists(fixture.Moved));
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await OfflineApplicationCommand.ExecuteAsync([.. args, "--mode", "apply"], new(output, error), true, Token, fixture.Verifier, catalog: fixture.Catalog));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("preflight-required", json.RootElement.GetProperty("data").GetProperty("result").GetProperty("residencyProof").GetString());
        output.GetStringBuilder().Clear();
        Assert.Equal(73, await OfflineApplicationCommand.ExecuteAsync([.. args, "--mode", "apply"], new(output, error), true, Token, fixture.Verifier, catalog: fixture.Catalog));
        Assert.Contains("output_exists", output.ToString());
    }

    private static async Task<OfflineBundleResult> Dependency(OfflineEnvironmentTests.Fixture fixture, string? damage = null)
    {
        var installer = damage switch { "undeclared" => "other.exe", "script" => "setup.cmd", "bad-msi-switch" or "valid-msi" => "setup.msi", _ => "ollama.exe" };
        if (installer != "ollama.exe") File.WriteAllText(Path.Combine(fixture.Source, installer), "fixture");
        var action = new { component = damage == "unknown-component" ? "unknown" : "dependency.ollama", kind = damage is "bad-msi-switch" or "valid-msi" ? "msi" : "exe", installer,
            arguments = damage switch { "bad-msi-switch" => new[] { "/x" }, "valid-msi" => ["INSTALLDIR=C:\\Offline Models"], _ => ["/quiet", "/norestart"] } };
        File.WriteAllText(Path.Combine(fixture.Source, "offline-actions.json"), JsonSerializer.Serialize(new { schemaVersion = 1, actions = new[] { action } }));
        File.WriteAllText(Path.Combine(fixture.Source, OfflineEnvironmentBundle.SourceName), JsonSerializer.Serialize(new { schemaVersion = 1, components = new[]
        {
            new { kind = "runtime", id = "localai", version = "0.0.0-test", files = new[] { "offline-actions.json" } },
            new { kind = "dependency", id = "dependency.ollama", version = "fixture", files = new[] { damage == "undeclared" ? "ollama.exe" : installer } },
        } }));
        return await fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token);
    }

    private static async Task<OfflineBundleResult> Models(OfflineEnvironmentTests.Fixture fixture, string? damage = null)
    {
        var model = fixture.Catalog.Models.First(item => item.Lifecycle != LocalModelLifecycle.Disabled && item.InstallPolicy != LocalModelInstallPolicy.Never).Tag;
        var split = model.Split(':'); var path = "models/manifests/registry.ollama.ai/library/" + split[0] + "/" + (split.Length == 2 ? split[1] : "latest");
        byte[] config = [1, 2]; byte[] weights = [3, 4, 5];
        var configHash = Convert.ToHexStringLower(SHA256.HashData(config)); var weightHash = Convert.ToHexStringLower(SHA256.HashData(weights));
        var configPath = "models/blobs/sha256-" + configHash; var weightPath = "models/blobs/sha256-" + weightHash;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Source, path))!); Directory.CreateDirectory(Path.Combine(fixture.Source, "models", "blobs"));
        File.WriteAllBytes(Path.Combine(fixture.Source, configPath), config); File.WriteAllBytes(Path.Combine(fixture.Source, weightPath), weights);
        File.WriteAllText(Path.Combine(fixture.Source, path), JsonSerializer.Serialize(new { schemaVersion = 2,
            config = new { digest = "sha256:" + configHash, size = 2 },
            layers = new[] { new { digest = "sha256:" + (damage == "digest" ? configHash : weightHash), size = damage == "size" ? 99 : 3 } } }));
        var files = new List<string> { path, configPath, weightPath };
        if (damage == "missing-blob") files.Remove(weightPath);
        if (damage == "extra-proof") { File.WriteAllText(Path.Combine(fixture.Source, "residency.json"), "old proof"); files.Add("residency.json"); }
        File.WriteAllText(Path.Combine(fixture.Source, OfflineEnvironmentBundle.SourceName), JsonSerializer.Serialize(new { schemaVersion = 1,
            components = new[] { new { kind = "model", id = model, version = "fixture", files = files.ToArray() } } }));
        return await fixture.Bundle.BuildAsync(fixture.Source, fixture.Output, fixture.Catalog, Token);
    }

    private sealed class Runner(Func<string, IReadOnlyList<string>, ProcessResult> callback) : IProcessRunner
    {
        internal int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(callback(executable, arguments)); }
    }
}
