using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using Microsoft.Win32;

namespace LocalAi.IntegrationTests;

public sealed class OfflineUninstallSetupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preview_lists_the_pinned_installer_and_per_user_destination_without_writes()
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build(includeUninstaller: true);
        var plan = await fixture.Application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request, fixture.Source.Run,
            fixture.Source.Materials.Catalog, fixture.Source.Layout, Token);
        Assert.Equal("LocalAi.Installer.exe", plan.Uninstall!.Source); Assert.Equal(fixture.Installed, plan.Uninstall.Destination);
        Assert.Equal("current-user", plan.Uninstall.RegistryScope); Assert.Equal(fixture.Registration.RegistrySubKey, plan.Uninstall.RegistrySubKey);
        Assert.Equal(new FileInfo(fixture.Material).Length, plan.Uninstall.Bytes); Assert.Equal(64, plan.Uninstall.Sha256.Length);
        Assert.Null(fixture.Registration.Read(fixture.Source.Layout)); Assert.False(Directory.Exists(fixture.Source.Run));
    }

    [Fact]
    public async Task Apply_registers_real_private_registry_entry_after_runtime_with_durable_intent_and_verified_copy()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging and private registry are required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build(includeUninstaller: true);
        fixture.Registration.BeforeWrite = path =>
        {
            Assert.True(File.Exists(fixture.Source.Layout.LauncherPath));
            Assert.NotEqual(fixture.Material, path); Assert.Equal("pinned installer fixture", File.ReadAllText(path));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "staging must remain protected"));
            using var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(fixture.Source.Run, "journal-*.json")));
            Assert.Equal("configuration:uninstall", journal.Snapshot.Steps[^1].Description);
            Assert.Equal(InstallerRunStepStatus.Running, journal.Snapshot.Steps[^1].Status);
        };
        // Mutating source after trust has been established cannot replace the retained copy.
        fixture.Runner.OnActivation = () => File.WriteAllText(fixture.Material, "changed bundle");
        var result = await fixture.Apply(built);
        Assert.Equal("applied", result.State); Assert.Empty(result.PendingSteps); Assert.False(result.ReadyForUse);
        Assert.Equal("pinned installer fixture", File.ReadAllText(fixture.Installed));
        var registration = Assert.IsType<UninstallEntry>(fixture.Registration.Read(fixture.Source.Layout));
        Assert.Equal("0.0.0-test", registration.DisplayVersion); Assert.Equal(fixture.Source.Layout.Root, registration.InstallLocation);
        Assert.Equal("\"" + fixture.Installed + "\" --uninstall", registration.UninstallString);
        Assert.Equal("uninstall", result.Steps[^1].Id);
    }

    [Theory]
    [InlineData("runtime-not-selected")]
    [InlineData("missing-material")]
    [InlineData("old-schema")]
    [InlineData("foreign-registration")]
    public async Task Invalid_registration_requests_are_refused_before_runtime_mutation(string failure)
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build(includeUninstaller: failure != "missing-material");
        if (failure == "foreign-registration") fixture.Registration.OverrideRead = new("Local AI Toolkit", "foreign", "MrAliev", "foreign", Path.GetTempPath(), 0);
        var request = fixture.Request with { InstallRuntime = failure != "runtime-not-selected", ModelsOutput = fixture.Source.Models,
            SchemaVersion = failure == "old-schema" ? 3 : 4 };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, request,
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token));
        Assert.Equal(0, fixture.Runner.Calls); Assert.False(Directory.Exists(fixture.Source.Layout.Root));
    }

    [Theory]
    [InlineData("registry-failure")]
    [InlineData("readback-mismatch")]
    [InlineData("changed-registration")]
    public async Task Registration_failure_preserves_runtime_and_prevents_later_model_import(string failure)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Protected Windows staging and private registry are required.");
        using var fixture = new Fixture(); var built = await fixture.Source.Build(includeUninstaller: true);
        fixture.Registration.Failure = failure;
        if (failure == "changed-registration") fixture.Runner.OnActivation = () =>
            fixture.Registration.OverrideRead = new("changed", "changed", "MrAliev", "changed", fixture.Source.Layout.Root, 0);
        var result = await fixture.Apply(built, fixture.Request with { ModelsOutput = fixture.Source.Models });
        Assert.Equal("failed", result.State); Assert.Contains("configuration:uninstall", result.PendingSteps); Assert.Contains("models:bundled", result.PendingSteps);
        Assert.True(File.Exists(fixture.Source.Layout.LauncherPath)); Assert.False(Directory.Exists(fixture.Source.Models));
        Assert.Equal("completed", result.Steps[0].State);
        if (failure == "changed-registration") Assert.Equal(0, fixture.Registration.Writes);
        using var journal = InstallerRunJournal.Load(result.JournalPath);
        Assert.Equal(InstallerRunStepStatus.Failed, journal.Snapshot.Steps[^1].Status);
    }

    [Fact]
    public async Task Default_request_does_not_access_uninstall_registration()
    {
        using var fixture = new Fixture(); var built = await fixture.Source.Build();
        await fixture.Application.PlanExecutionAsync(fixture.Bundle, built.ManifestSha256, fixture.Request with { RegisterUninstall = false },
            fixture.Source.Run, fixture.Source.Materials.Catalog, fixture.Source.Layout, Token);
        Assert.Equal(0, fixture.Registration.Reads); Assert.Equal(0, fixture.Registration.Writes);
    }

    private sealed class Fixture : IDisposable
    {
        public OfflineExecutionTests.Fixture Source { get; } = new();
        public Registration Registration { get; } = new();
        public Runner Runner { get; }
        public string Bundle => Source.Materials.Output;
        public string Material => Path.Combine(Bundle, "LocalAi.Installer.exe");
        public string Installed => Path.Combine(Source.Layout.BinRoot, "uninstall", "LocalAi.Installer.exe");
        public OfflineExecutionRequest Request => Source.Request with { SchemaVersion = 4, Dependencies = [], ModelsOutput = null, RegisterUninstall = true };
        public OfflineBundleApplication Application => new(Source.Materials.Verifier, Runner, uninstallRegistration: Registration);
        public Fixture() => Runner = new(Source.Layout);
        public Task<OfflineExecutionResult> Apply(OfflineBundleResult built, OfflineExecutionRequest? request = null) =>
            Application.ExecuteAsync(Bundle, built.ManifestSha256, request ?? Request, Source.Run, Source.Materials.Catalog, Source.Layout, Token);
        public void Dispose()
        {
            Source.Dispose();
            if (OperatingSystem.IsWindows()) Registry.CurrentUser.DeleteSubKeyTree(Registration.RegistryRoot, throwOnMissingSubKey: false);
        }
    }

    private sealed class Registration : IOfflineUninstallRegistration
    {
        public string RegistryRoot { get; } = @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N");
        public string RegistrySubKey => RegistryRoot + @"\Uninstall\LocalAi";
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Action<string>? BeforeWrite { get; set; }
        public string? Failure { get; set; }
        public UninstallEntry? OverrideRead { get; set; }
        public UninstallEntry? Read(InstallationLayout layout)
        {
            Reads++;
            if (OverrideRead is not null) return OverrideRead;
            return new OfflineUninstallRegistration(RegistrySubKey).Read(layout);
        }
        public UninstallEntry Register(InstallationLayout layout, string version, string source)
        {
            BeforeWrite?.Invoke(source); Writes++;
            if (Failure == "registry-failure") throw new IOException("controlled registry failure");
            var written = new OfflineUninstallRegistration(RegistrySubKey).Register(layout, version, source);
            if (Failure == "readback-mismatch") OverrideRead = written with { DisplayVersion = "changed after write" };
            return written;
        }
    }

    private sealed class Runner(InstallationLayout layout) : IProcessRunner
    {
        public int Calls { get; private set; }
        public Action? OnActivation { get; set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            if (arguments[0] == "activate")
            { File.WriteAllText(layout.CurrentPointerPath, "{\"schemaVersion\":1,\"version\":\"abcdef123456\"}"); OnActivation?.Invoke(); }
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        }
    }
}
