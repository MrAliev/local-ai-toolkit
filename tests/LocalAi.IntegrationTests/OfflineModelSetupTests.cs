using System.Text.Json;
using LocalAi.Cli;
using LocalAi.Contracts;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.IntegrationTests;

public sealed class OfflineModelSetupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_plan_reports_selected_configuration_instead_of_an_unconditional_manual_setup_notice()
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        var requestFile = Path.Combine(Path.GetDirectoryName(fixture.Source.Run)!, "request.json");
        File.WriteAllText(requestFile, JsonSerializer.Serialize(fixture.Request, OfflineEnvironmentBundle.Json));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await OfflineExecuteCommand.ExecuteAsync(["--bundle", fixture.Bundle, "--manifest-sha256", built.ManifestSha256,
            "--request", requestFile, "--out", fixture.Source.Run], new(output, error), true, Token,
            fixture.Source.Materials.Verifier, new NoProcess(), fixture.Source.Materials.Catalog, fixture.Source.Layout);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString()); var data = json.RootElement.GetProperty("data");
        Assert.Contains("configureModels=true", data.GetProperty("modelNotice").GetString());
        Assert.Equal(fixture.Source.Models, data.GetProperty("result").GetProperty("modelSetup").GetProperty("directory").GetString());
        Assert.False(File.Exists(fixture.Record)); Assert.False(Directory.Exists(fixture.Source.Run));
    }

    [Fact]
    public async Task Plan_exposes_record_and_imported_root_without_probing_or_writing()
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        var plan = await fixture.Application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request,
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token);
        Assert.Equal(fixture.Source.Models, plan.ModelSetup!.Directory);
        Assert.Equal(fixture.Record, plan.ModelSetup.RecordPath); Assert.Null(plan.ModelSetup.BeforeSha256);
        Assert.DoesNotContain("ollama-model-root-configuration", plan.RemainingRequirements);
        Assert.Contains("target-broker-preflight", plan.RemainingRequirements);
        Assert.Equal(0, fixture.Probe.Calls); Assert.False(File.Exists(fixture.Record)); Assert.False(Directory.Exists(fixture.Source.Models));
    }

    [Fact]
    public async Task Apply_imports_then_records_verified_ollama_and_root_after_durable_intent()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        fixture.Probe.Before = () =>
        {
            Assert.True(Directory.Exists(fixture.Source.Models));
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(fixture.Source.Run, "journal-*.json")));
            Assert.Equal("configuration:ollama-model-root", journal.Snapshot.Steps[^1].Description);
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status);
        };
        var result = await fixture.Apply(built);
        Assert.Equal("applied", result.State); Assert.Empty(result.PendingSteps); Assert.False(result.ReadyForUse);
        var record = Assert.IsType<OllamaLaunchRecord>(new OllamaLaunchRecordStore(fixture.Source.Layout.Root).Read());
        Assert.Equal(fixture.Executable, record.ExecutablePath); Assert.Equal(fixture.Source.Models, record.ModelsDirectory);
        Assert.Equal("fixture", record.Version); Assert.Equal("ollama-model-root", result.Steps[^1].Id);
        using var saved = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunOutcome.Completed, saved.Snapshot.Outcome);
    }

    [Fact]
    public async Task An_unverified_ollama_leaves_configuration_pending_and_does_not_invent_a_launch_path()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build(); fixture.Probe.Missing = true;
        var result = await fixture.Apply(built);
        Assert.Equal("failed", result.State); Assert.Equal(["configuration:ollama-model-root"], result.PendingSteps);
        Assert.Equal("ollama-installation-unverified", result.Steps[^1].Detail);
        Assert.True(Directory.Exists(fixture.Source.Models)); Assert.False(File.Exists(fixture.Record));
    }

    [Fact]
    public async Task A_concurrent_launch_record_change_is_preserved()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        fixture.Probe.Before = () => File.WriteAllText(fixture.Record, "concurrent change");
        var result = await fixture.Apply(built);
        Assert.Equal("failed", result.State); Assert.Equal("ollama-launch-record-changed", result.Steps[^1].Detail);
        Assert.Equal("concurrent change", File.ReadAllText(fixture.Record));
    }

    [Fact]
    public async Task A_locked_record_is_preserved_and_failure_is_journaled()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows file sharing is required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        new OllamaLaunchRecordStore(fixture.Source.Layout.Root).Save(fixture.Executable, "before");
        using var locked = new FileStream(fixture.Record, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await fixture.Apply(built);
        Assert.Equal("failed", result.State); Assert.Equal("before", new OllamaLaunchRecordStore(fixture.Source.Layout.Root).Read()!.Version);
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunStepStatus.Failed, journal.Snapshot.Steps[^1].Status); Assert.Contains("configuration:ollama-model-root", result.PendingSteps);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task Invalid_setup_requests_are_refused_before_import(int schema, bool models)
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256,
            fixture.Request with { SchemaVersion = schema, ModelsOutput = models ? fixture.Source.Models : null, Dependencies = ["dependency.ollama"] },
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token));
        Assert.Equal(0, fixture.Probe.Calls); Assert.False(Directory.Exists(fixture.Source.Models));
    }

    [Fact]
    public void Unknown_request_fields_are_refused_instead_of_silently_skipping_future_setup()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OfflineExecutionRequest>(
            "{\"schemaVersion\":4,\"dependencies\":[],\"installRuntime\":false,\"modelsOutput\":null,\"futureSetup\":true}", OfflineEnvironmentBundle.Json));
    }

    [Fact]
    public async Task Default_import_does_not_probe_or_modify_an_existing_record()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging is required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        File.WriteAllText(fixture.Record, "preserve");
        var result = await fixture.Apply(built, fixture.Request with { SchemaVersion = 1, ConfigureModels = false });
        Assert.Equal("applied", result.State); Assert.Equal("preserve", File.ReadAllText(fixture.Record)); Assert.Equal(0, fixture.Probe.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        public OfflineExecutionTests.Fixture Source { get; } = new();
        public Probe Probe { get; }
        public string Bundle => Source.Materials.Output;
        public string Executable => Path.Combine(Path.GetDirectoryName(Source.Run)!, "ollama.exe");
        public string Record => Path.Combine(Source.Layout.Root, OllamaLaunchRecord.FileName);
        public OfflineExecutionRequest Request => Source.Request with { SchemaVersion = 4, Dependencies = [], InstallRuntime = false, ConfigureModels = true };
        public OfflineBundleApplication Application => new(Source.Materials.Verifier, new NoProcess(), installedApplications: Probe);
        public Fixture()
        { Directory.CreateDirectory(Source.Layout.Root); File.WriteAllText(Executable, "verified fixture"); Probe = new(Executable); }
        public Task<OfflineExecutionResult> Apply(OfflineBundleResult bundle, OfflineExecutionRequest? request = null) =>
            Application.ExecuteAsync(Bundle, bundle.ManifestSha256, request ?? Request, Source.Run, Source.Materials.Catalog, Source.Layout, Token);
        public void Dispose() => Source.Dispose();
    }

    private sealed class Probe(string executable) : IInstalledApplicationProbe
    {
        public int Calls { get; private set; }
        public bool Missing { get; set; }
        public Action? Before { get; set; }
        public Task<InstalledApplicationMetadata?> FindOllamaAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Before?.Invoke();
            return Task.FromResult<InstalledApplicationMetadata?>(Missing ? null : new("Ollama", "fixture", Path.GetDirectoryName(executable), executable, "fixture", "fixture"));
        }
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Model configuration must not launch a process.");
    }
}
