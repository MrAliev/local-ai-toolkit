using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.IntegrationTests;

public sealed class OfflinePathSetupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_defaults_to_a_plan_that_exposes_the_exact_path_change_and_notice()
    {
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var request = Path.Combine(Path.GetDirectoryName(fixture.Run)!, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(fixture.Request, OfflineEnvironmentBundle.Json));
        using var output = new StringWriter(); using var error = new StringWriter();
        var store = new PathStore("C:\\before");
        var exit = await OfflineExecuteCommand.ExecuteAsync(["--bundle", fixture.Bundle, "--manifest-sha256", built.ManifestSha256,
            "--request", request, "--out", fixture.Run], new(output, error), true, Token,
            fixture.Materials.Materials.Verifier, new Runner(), fixture.Catalog, fixture.Layout, store);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
        Assert.Contains("registerPaths=true", data.GetProperty("pathNotice").GetString());
        Assert.Equal("C:\\before", data.GetProperty("result").GetProperty("userPath").GetProperty("before").GetString());
        Assert.Equal(0, store.Writes); Assert.False(Directory.Exists(fixture.Run));
    }

    [Fact]
    public async Task Preview_preserves_existing_order_and_deduplicates_node_without_changing_environment()
    {
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var nodeDirectory = Path.GetDirectoryName(fixture.Materials.Node)!;
        var before = "C:\\already-first;\"" + nodeDirectory.ToUpperInvariant() + "\\\"";
        var store = new PathStore(before);
        var runner = new Runner();
        var plan = await new OfflineBundleApplication(fixture.Materials.Materials.Verifier, runner, store)
            .PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Catalog, fixture.Layout, Token);
        Assert.Equal(before, plan.UserPath!.Before);
        Assert.Equal(before + ";" + fixture.Bin, plan.UserPath.After);
        Assert.Equal([nodeDirectory, fixture.Bin], plan.UserPath.Directories);
        Assert.Contains("new-process-environment-required", plan.RemainingRequirements);
        Assert.DoesNotContain("npm-path-configuration", plan.RemainingRequirements);
        Assert.Equal(0, store.Writes); Assert.Equal(0, runner.Calls); Assert.False(Directory.Exists(fixture.Run));
    }

    [Fact]
    public async Task Preview_puts_new_directories_in_front_of_a_trailing_separator()
    {
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var nodeDirectory = Path.GetDirectoryName(fixture.Materials.Node)!;
        var store = new PathStore("C:\\already-first;");
        var plan = await new OfflineBundleApplication(fixture.Materials.Materials.Verifier, new Runner(), store)
            .PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Run, fixture.Catalog, fixture.Layout, Token);
        Assert.Equal("C:\\already-first;" + nodeDirectory + ";" + fixture.Bin + ";", plan.UserPath!.After);
    }

    [Fact]
    public async Task Apply_publishes_npm_first_then_flushes_path_intent_before_writing_and_reads_back()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var store = new PathStore("C:\\existing");
        store.BeforeWrite = () =>
        {
            Assert.True(File.Exists(Path.Combine(fixture.Bin, "scip-typescript.cmd")));
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(fixture.Run, "journal-*.json")));
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status);
            Assert.Equal("configuration:user-path", journal.Snapshot.Steps[^1].Description);
        };
        var result = await fixture.Apply(built, store, new Runner());
        Assert.Equal("applied", result.State); Assert.Empty(result.PendingSteps); Assert.False(result.ReadyForUse);
        Assert.Equal("user-path", result.Steps[^1].Id); Assert.Equal(1, store.Writes);
        Assert.Equal(result.Plan.UserPath!.After, store.Value);
        using var saved = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunOutcome.Completed, saved.Snapshot.Outcome);
        Assert.All(saved.Snapshot.Steps, step => Assert.Equal(InstallerRunStepStatus.Completed, step.Status));
    }

    [Theory]
    [InlineData("changed-during-install")]
    [InlineData("write-failed")]
    [InlineData("readback-mismatch")]
    [InlineData("shim-missing")]
    public async Task Failed_configuration_keeps_installed_files_and_pending_step_without_overwriting_again(string failure)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var store = new PathStore("C:\\existing") { Failure = failure };
        var runner = new Runner { MakeShim = failure != "shim-missing", DuringRun = () =>
        { if (failure == "changed-during-install") store.Value = "C:\\concurrent-change"; } };
        var result = await fixture.Apply(built, store, runner);
        Assert.Equal("failed", result.State); Assert.Equal(["configuration:user-path"], result.PendingSteps);
        Assert.Equal(failure switch { "changed-during-install" => "user-path-changed", "shim-missing" => "user-path-target-missing",
            "write-failed" => "user-path-write-failed", _ => "user-path-readback-mismatch" }, result.Steps[^1].Detail);
        Assert.True(Directory.Exists(fixture.Materials.Output)); Assert.Equal("completed", result.Steps[0].State);
        Assert.Equal(failure is "write-failed" or "readback-mismatch" ? 1 : 0, store.Writes);
        if (failure == "changed-during-install") Assert.Equal("C:\\concurrent-change", store.Value);
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunOutcome.Failed, journal.Snapshot.Outcome);
        Assert.Equal(InstallerRunStepStatus.Failed, journal.Snapshot.Steps[^1].Status);
    }

    [Fact]
    public async Task Dependency_failure_does_not_read_or_write_configuration_after_planning()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var store = new PathStore(null);
        var result = await fixture.Apply(built, store, new Runner { Exit = 1 });
        Assert.Equal("failed", result.State); Assert.Contains("configuration:user-path", result.PendingSteps);
        Assert.Equal(0, store.Writes); Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Already_registered_directories_are_checked_but_not_written_again()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var store = new PathStore(Path.GetDirectoryName(fixture.Materials.Node) + ";" + fixture.Bin);
        var result = await fixture.Apply(built, store, new Runner());
        Assert.Equal("applied", result.State); Assert.Equal(0, store.Writes); Assert.Empty(result.PendingSteps);
    }

    [Fact]
    public async Task Runtime_registration_waits_for_verified_launcher_activation()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new OfflineExecutionTests.Fixture(); var built = await fixture.Build();
        var store = new PathStore(null) { BeforeWrite = () => Assert.True(File.Exists(fixture.Layout.LauncherPath)) };
        var request = fixture.Request with { SchemaVersion = 3, Dependencies = [], ModelsOutput = null, RegisterPaths = true };
        var result = await new OfflineBundleApplication(fixture.Materials.Verifier, new Runner { ActivationLayout = fixture.Layout }, store)
            .ExecuteAsync(fixture.Materials.Output, built.ManifestSha256, request, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token);
        Assert.Equal("applied", result.State); Assert.Equal(fixture.Layout.LauncherDirectory, store.Value);
        Assert.Equal(["runtime", "configuration"], result.Steps.Select(step => step.Kind));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Older_schema_cannot_silently_ignore_explicit_path_registration(int schema)
    {
        using var fixture = new OfflineExecutionTests.Fixture(); var built = await fixture.Build();
        var store = new PathStore(null);
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfflineBundleApplication(fixture.Materials.Verifier, new Runner(), store)
            .PlanExecutionAsync(fixture.Materials.Output, built.ManifestSha256,
                fixture.Request with { SchemaVersion = schema, RegisterPaths = true }, fixture.Run, fixture.Materials.Catalog, fixture.Layout, Token));
        Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Opt_out_never_reads_the_user_environment()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Materials.Build();
        var store = new PathStore(null);
        var result = await fixture.Apply(built, store, new Runner(), fixture.Request with { SchemaVersion = 2, RegisterPaths = false });
        Assert.Equal("applied", result.State); Assert.Null(result.Plan.UserPath);
        Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
    }

    private sealed class PathStore(string? value) : IUserPathStore
    {
        public string? Value { get; set; } = value;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public Action? BeforeWrite { get; set; }
        public string? Failure { get; init; }
        public string? Read() { Reads++; return Value; }
        public void Write(string after)
        {
            BeforeWrite?.Invoke(); Writes++;
            if (Failure == "write-failed") throw new IOException("fixture write failed");
            Value = Failure == "readback-mismatch" ? "C:\\changed-after-write" : after;
        }
    }

    private sealed class Runner : IProcessRunner, IProcessWorkingDirectoryRunner
    {
        public int Calls { get; private set; }
        public int Exit { get; init; }
        public bool MakeShim { get; init; } = true;
        public Action? DuringRun { get; init; }
        public InstallationLayout? ActivationLayout { get; init; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (ActivationLayout is not null && arguments.Count > 0 && arguments[0] == "activate")
                File.WriteAllText(ActivationLayout.CurrentPointerPath, "{\"schemaVersion\":1,\"version\":\"abcdef123456\"}");
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        }
        public Task<ProcessResult> RunInDirectoryAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++; DuringRun?.Invoke();
            if (Exit == 0)
            {
                var package = Path.Combine(workingDirectory, "node_modules", "@sourcegraph", "scip-typescript"); Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@sourcegraph/scip-typescript\",\"version\":\"0.4.0\"}");
                if (MakeShim)
                { var bin = Path.Combine(workingDirectory, "node_modules", ".bin"); Directory.CreateDirectory(bin); File.WriteAllText(Path.Combine(bin, "scip-typescript.cmd"), "fixture"); }
            }
            return Task.FromResult(new ProcessResult(Exit, "", "", false, false));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public OfflineNpmTests.Fixture Materials { get; } = new();
        public string Bundle => Materials.Materials.Output;
        public string Run => Materials.Output + "-run";
        public string Bin => Path.Combine(Materials.Output, "node_modules", ".bin");
        public LocalAi.Contracts.ModelRoutingCatalogDocument Catalog => Materials.Materials.Catalog;
        public InstallationLayout Layout { get; }
        public OfflineExecutionRequest Request => new(3, [], false, null, [new(Materials.Component, Materials.Node, Materials.NpmCli, Materials.Output)], true);
        public Fixture()
        { var profile = Path.Combine(Path.GetDirectoryName(Run)!, "profile"); Directory.CreateDirectory(profile); Layout = InstallationLayout.FromLocalAppData(profile); }
        public Task<OfflineExecutionResult> Apply(OfflineBundleResult built, PathStore store, Runner runner, OfflineExecutionRequest? request = null)
            => new OfflineBundleApplication(Materials.Materials.Verifier, runner, store).ExecuteAsync(Bundle, built.ManifestSha256,
                request ?? Request, Run, Catalog, Layout, Token);
        public void Dispose() => Materials.Dispose();
    }
}
