using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAi.Contracts;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.IntegrationTests;

public sealed class OfflineExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("plan", 0, null)]
    [InlineData("apply", 70, "failed")]
    public async Task Cli_defaults_to_a_side_effect_free_plan_and_preserves_failed_run_details(string mode, int expected, string? state)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var requestPath = Path.Combine(Path.GetDirectoryName(fixture.Run)!, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(fixture.Request, OfflineEnvironmentBundle.Json));
        using var output = new StringWriter(); using var error = new StringWriter();
        var runner = new Runner((_, _) => new(42, "", "", false, false));
        string[] args = ["--bundle", fixture.Materials.Output, "--manifest-sha256", built.ManifestSha256,
            "--request", requestPath, "--out", fixture.Run, .. mode == "apply" ? new[] { "--mode", mode } : Array.Empty<string>()];
        var exit = await OfflineExecuteCommand.ExecuteAsync(args, new(output, error), true, Token,
            fixture.Materials.Verifier, runner, fixture.Materials.Catalog, fixture.Layout);
        Assert.Equal(expected, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains("registerUninstall=true", json.RootElement.GetProperty("data").GetProperty("uninstallNotice").GetString());
        Assert.Equal(mode == "plan", json.RootElement.GetProperty("ok").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(mode, data.GetProperty("mode").GetString());
        if (state is null)
        { Assert.Equal(0, runner.Calls); Assert.False(Directory.Exists(fixture.Run)); }
        else
        {
            Assert.Equal(state, data.GetProperty("result").GetProperty("state").GetString());
            Assert.False(data.GetProperty("result").GetProperty("readyForUse").GetBoolean());
            Assert.Equal(1, runner.Calls); Assert.True(Directory.Exists(fixture.Run));
        }
        Assert.False(Directory.Exists(fixture.Models)); Assert.False(Directory.Exists(fixture.Layout.Root));
    }

    [Theory]
    [InlineData("invalid-json", 65, "offline_execution_refused")]
    [InlineData("remote-models", 65, "offline_execution_refused")]
    [InlineData("existing-output", 73, "output_exists")]
    public async Task Cli_rejects_invalid_requests_before_any_effect(string damage, int expected, string code)
    {
        using var fixture = new Fixture(); var built = await fixture.Build();
        var requestPath = Path.Combine(Path.GetDirectoryName(fixture.Run)!, "request.json");
        File.WriteAllText(requestPath, damage == "invalid-json" ? "{broken" : JsonSerializer.Serialize(
            fixture.Request with { ModelsOutput = damage == "remote-models" ? @"\\invalid-server\share" : fixture.Models }, OfflineEnvironmentBundle.Json));
        if (damage == "existing-output") Directory.CreateDirectory(fixture.Run);
        using var output = new StringWriter(); using var error = new StringWriter();
        var runner = new Runner((_, _) => throw new InvalidOperationException("Must not launch."));
        var exit = await OfflineExecuteCommand.ExecuteAsync(["--bundle", fixture.Materials.Output, "--manifest-sha256", built.ManifestSha256,
            "--request", requestPath, "--out", fixture.Run, "--mode", "apply"], new(output, error), true, Token,
            fixture.Materials.Verifier, runner, fixture.Materials.Catalog, fixture.Layout);
        Assert.Equal(expected, exit); Assert.Contains(code, output.ToString()); Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(fixture.Models)); Assert.False(Directory.Exists(fixture.Layout.Root));
    }

    [Fact]
    public async Task Preview_has_no_effects_and_apply_records_intent_before_dependencies_runtime_and_models()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var calls = new List<string>();
        var runner = new Runner((executable, arguments) =>
        {
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(fixture.Run, "journal-*.json")));
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status);
            Assert.False(Directory.Exists(fixture.Models));
            if (arguments.Count > 0 && arguments[0] == "activate")
            {
                calls.Add("runtime");
                Assert.Equal(fixture.Layout.LauncherPath, executable);
                Assert.Equal("abcdef123456", arguments[1]);
                File.WriteAllText(fixture.Layout.CurrentPointerPath, "{\"schemaVersion\":1,\"version\":\"abcdef123456\"}");
            }
            else calls.Add("dependency");
            return new(0, "", "", false, false);
        });
        var application = new OfflineBundleApplication(fixture.Materials.Verifier, runner);
        var plan = await application.PlanExecutionAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Single(plan.Dependencies); Assert.NotNull(plan.Models); Assert.Equal(fixture.Layout.Root, plan.RuntimeRoot);
        Assert.Empty(calls); Assert.False(Directory.Exists(fixture.Run)); Assert.False(Directory.Exists(fixture.Models)); Assert.False(Directory.Exists(fixture.Layout.Root));
        var result = await application.ExecuteAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal("applied", result.State); Assert.Equal(["dependency", "runtime"], calls);
        Assert.Equal(["dependency", "runtime", "models"], result.Steps.Select(step => step.Kind));
        Assert.Empty(result.PendingSteps); Assert.True(result.RecordingComplete); Assert.False(result.ReadyForUse);
        Assert.True(Directory.Exists(fixture.Models)); Assert.True(File.Exists(fixture.Layout.LauncherPath));
        using var saved = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunOutcome.Completed, saved.Snapshot.Outcome);
        Assert.All(saved.Snapshot.Steps, step => Assert.Equal(InstallerRunStepStatus.Completed, step.Status));
        Assert.True(File.Exists(Path.Combine(fixture.Run, "plan.json"))); Assert.True(File.Exists(Path.Combine(fixture.Run, "result.json")));
    }

    [Theory]
    [InlineData(42, "failed", false)]
    [InlineData(3010, "reboot-required", true)]
    [InlineData(1641, "reboot-required", true)]
    public async Task Nonzero_dependency_outcomes_stop_before_runtime_and_model_import(int exit, string state, bool reboot)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((_, _) => new(exit, "", "", false, false));
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, runner).ExecuteAsync(fixture.Materials.Output, built.ManifestSha256,
            fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal(state, result.State); Assert.Equal(1, runner.Calls);
        Assert.Equal(reboot, Assert.Single(result.Steps).RebootRequired);
        Assert.False(Directory.Exists(fixture.Models)); Assert.False(Directory.Exists(fixture.Layout.Root));
        Assert.Contains("runtime:localai", result.PendingSteps); Assert.Contains("models:bundled", result.PendingSteps);
        using var saved = InstallerRunJournal.Load(result.JournalPath);
        if (reboot) Assert.Null(saved.Snapshot.Outcome);
        else Assert.Equal(InstallerRunOutcome.Failed, saved.Snapshot.Outcome);
    }

    [Theory]
    [InlineData(true, false, "timed-out")]
    [InlineData(false, true, "cancelled")]
    public async Task Interrupted_dependency_stops_the_sequence(bool timeout, bool cancelled, string state)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((_, _) => new(null, "", "", timeout, cancelled));
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, runner).ExecuteAsync(fixture.Materials.Output, built.ManifestSha256,
            fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal(state, result.State); Assert.Equal(1, runner.Calls); Assert.False(Directory.Exists(fixture.Models));
        Assert.True(result.RecordingComplete);
    }

    [Fact]
    public async Task Runtime_full_verification_fails_before_any_dependency_or_run_output()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(invalidPackageMetadata: true); var built = await fixture.Build();
        var runner = new Runner((_, _) => throw new InvalidOperationException("Must not launch."));
        await Assert.ThrowsAsync<ReleaseVerificationException>(() => new OfflineBundleApplication(fixture.Materials.Verifier, runner)
            .ExecuteAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token));
        Assert.Equal(0, runner.Calls); Assert.False(Directory.Exists(fixture.Run)); Assert.False(Directory.Exists(fixture.Layout.Root));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("bad-models")]
    [InlineData("output-in-bundle")]
    public async Task Invalid_selections_are_refused_before_any_mutation(string damage)
    {
        using var fixture = new Fixture(); var built = await fixture.Build();
        var request = damage switch
        {
            "duplicate" => fixture.Request with { Dependencies = ["dependency.ollama", "dependency.ollama"] },
            "unknown" => fixture.Request with { Dependencies = ["dependency.unknown"] },
            "empty" => new(1, [], false, null),
            "bad-models" => fixture.Request with { ModelsOutput = fixture.Materials.Source },
            _ => fixture.Request,
        };
        var output = damage == "output-in-bundle" ? Path.Combine(fixture.Materials.Output, "run") : fixture.Run;
        var error = await Record.ExceptionAsync(() => new OfflineBundleApplication(fixture.Materials.Verifier, new Runner((_, _) => throw new InvalidOperationException()))
            .PlanExecutionAsync(fixture.Materials.Output, built.ManifestSha256, request, output, fixture.Materials.Catalog, fixture.Layout, Token));
        if (damage == "bad-models") Assert.IsType<IOException>(error); else Assert.IsType<InvalidDataException>(error);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Existing_run_directory_cannot_be_reused_or_overwritten()
    {
        using var fixture = new Fixture(); var built = await fixture.Build();
        Directory.CreateDirectory(fixture.Run); File.WriteAllText(Path.Combine(fixture.Run, "owner.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => new OfflineBundleApplication(fixture.Materials.Verifier, new Runner((_, _) => throw new InvalidOperationException()))
            .ExecuteAsync(fixture.Materials.Output, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Run, "owner.txt")));
    }

    [Fact]
    public async Task A_journal_write_failure_keeps_the_durable_intent_and_prevents_later_effects()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows file-sharing protection is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        FileStream? held = null;
        var runner = new Runner((_, _) =>
        {
            held = new FileStream(Assert.Single(Directory.GetFiles(fixture.Run, "journal-*.json")), FileMode.Open, FileAccess.Read, FileShare.Read);
            return new(0, "", "", false, false);
        });
        try
        {
            var result = await new OfflineBundleApplication(fixture.Materials.Verifier, runner).ExecuteAsync(fixture.Materials.Output, built.ManifestSha256,
                fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
            Assert.Equal("recording-failed", result.State); Assert.False(result.RecordingComplete); Assert.Equal(1, runner.Calls);
            using var saved = InstallerRunJournal.Load(result.JournalPath);
            Assert.Equal(InstallerRunStepStatus.Running, Assert.Single(saved.Snapshot.Steps).Status); Assert.Null(saved.Snapshot.Outcome);
            Assert.False(Directory.Exists(fixture.Layout.Root)); Assert.False(Directory.Exists(fixture.Models));
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task Unconfirmed_termination_keeps_an_interrupted_journal_and_preserved_installer_files()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, new Runner((_, _) =>
            throw new ProcessTerminationException(1, ProcessTerminationCause.Timeout, "fixture"))).ExecuteAsync(fixture.Materials.Output, built.ManifestSha256,
                fixture.Request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal("termination-unconfirmed", result.State);
        var retained = Assert.Single(result.Steps).RetainedDirectory;
        Assert.NotNull(retained); Assert.StartsWith(Path.Combine(Path.GetTempPath(), "localai-offline-"), Path.GetFullPath(retained), StringComparison.OrdinalIgnoreCase);
        try
        {
            Assert.True(File.Exists(Path.Combine(retained, "ollama.exe")));
            using var saved = InstallerRunJournal.Load(result.JournalPath);
            Assert.Null(saved.Snapshot.Outcome); Assert.Equal(InstallerRunStepStatus.Running, Assert.Single(saved.Snapshot.Steps).Status);
            Assert.False(Directory.Exists(fixture.Layout.Root));
        }
        finally { Directory.Delete(retained, recursive: true); }
    }

    [Fact]
    public async Task Changed_bundle_after_dependency_prevents_model_import_and_preserves_prior_results()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Build();
        var runner = new Runner((_, _) =>
        {
            File.AppendAllText(Path.Combine(fixture.Materials.Output, "ollama.exe"), "changed after verification");
            return new(0, "", "", false, false);
        });
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, runner).ExecuteAsync(fixture.Materials.Output, built.ManifestSha256,
            fixture.Request with { InstallRuntime = false }, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal("failed", result.State); Assert.Equal("completed", Assert.Single(result.Steps).State);
        Assert.Contains("models:bundled", result.PendingSteps); Assert.False(Directory.Exists(fixture.Models));
        using var saved = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunStepStatus.Failed, saved.Snapshot.Steps[^1].Status);
    }

    private sealed class Runner(Func<string, IReadOnlyList<string>, ProcessResult> callback) : IProcessRunner
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(callback(executable, arguments)); }
    }

    internal sealed class Fixture : IDisposable
    {
        public OfflineEnvironmentTests.Fixture Materials { get; }
        public string Run => Path.Combine(Path.GetDirectoryName(Materials.Source)!, "run");
        public string Models => Path.Combine(Path.GetDirectoryName(Materials.Source)!, "models");
        public InstallationLayout Layout { get; }
        public OfflineExecutionRequest Request => new(1, ["dependency.ollama"], true, Models);

        public Fixture(bool invalidPackageMetadata = false, bool signModels = false)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var file in LocalAiPackageLayout.PackageArtifactFiles)
                { using var entry = zip.CreateEntry(file, CompressionLevel.NoCompression).Open(); entry.Write([1, 2, 3]); }
                var metadata = JsonSerializer.Serialize(new { SchemaVersion = 1, ReleaseVersion = invalidPackageMetadata ? "wrong" : "0.0.0-test",
                    VersionDirectory = "abcdef123456", ModelCatalogVersion = ModelRoutingCatalogStore.LoadEmbedded().CatalogVersion,
                    ProtocolVersion = BrokerCompatibilityContract.ProtocolVersion, BuildCompatibilityId = BrokerCompatibilityContract.BuildCompatibilityId });
                using var document = zip.CreateEntry(ReleasePackageVerifier.PackageMetadataFileName, CompressionLevel.NoCompression).Open();
                document.Write(Encoding.UTF8.GetBytes(metadata));
            }
            Materials = new(bytes.ToArray(), signModels);
            var appData = Path.Combine(Path.GetDirectoryName(Materials.Source)!, "profile"); Directory.CreateDirectory(appData);
            Layout = InstallationLayout.FromLocalAppData(appData);
        }

        public async Task<OfflineBundleResult> Build(bool includeUninstaller = false)
        {
            File.WriteAllText(Path.Combine(Materials.Source, "offline-actions.json"), JsonSerializer.Serialize(new OfflineActionDocument(1,
                [new("dependency.ollama", "exe", "ollama.exe", [])]), OfflineEnvironmentBundle.Json));
            var model = Materials.Catalog.Models.First(model => model.Lifecycle != LocalModelLifecycle.Disabled && model.InstallPolicy != LocalModelInstallPolicy.Never).Tag;
            var parts = model.Split(':'); var manifest = "models/manifests/registry.ollama.ai/library/" + parts[0] + "/" + (parts.Length == 2 ? parts[1] : "latest");
            var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 42 })); var blob = "models/blobs/sha256-" + hash;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Materials.Source, manifest))!); Directory.CreateDirectory(Path.Combine(Materials.Source, "models", "blobs"));
            File.WriteAllBytes(Path.Combine(Materials.Source, blob), [42]);
            File.WriteAllText(Path.Combine(Materials.Source, manifest), JsonSerializer.Serialize(new { schemaVersion = 2,
                config = new { digest = "sha256:" + hash, size = 1 }, layers = new[] { new { digest = "sha256:" + hash, size = 1 } } }));
            if (includeUninstaller) File.WriteAllText(Path.Combine(Materials.Source, "LocalAi.Installer.exe"), "pinned installer fixture");
            File.WriteAllText(Path.Combine(Materials.Source, OfflineEnvironmentBundle.SourceName), JsonSerializer.Serialize(new OfflineSourceDocument(1,
                [new("runtime", "localai", "0.0.0-test", includeUninstaller ? ["offline-actions.json", "LocalAi.Installer.exe"] : ["offline-actions.json"]), new("dependency", "dependency.ollama", "fixture", ["ollama.exe"]),
                 new("model", model, "fixture", [manifest, blob])]), OfflineEnvironmentBundle.Json));
            return await Materials.Bundle.BuildAsync(Materials.Source, Materials.Output, Materials.Catalog, Token);
        }
        public void Dispose() => Materials.Dispose();
    }
}
