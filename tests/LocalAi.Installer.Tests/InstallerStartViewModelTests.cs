using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The front door. One executable installs, updates, repairs, reinstalls and removes, so what
/// it offers has to depend on what is actually on the machine — and where it cannot help, it
/// has to say why rather than present a button that fails.
/// </summary>
public sealed class InstallerStartViewModelTests : IDisposable
{
    // The language is process state now, so a class that asserts English says so. Run
    // after one that chose Russian, it would otherwise read that choice as its own — which
    // is exactly how this first failed.
    // xunit builds one instance per test, so this runs before each of them — a
    // static constructor runs once and lets whichever class went first decide.
    public InstallerStartViewModelTests() => InstallerCulture.Current = InstallerLanguage.English;


    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public void An_installed_machine_is_offered_everything_except_a_first_install()
    {
        var start = new InstallerStartViewModel(machine.LocalAppData);

        Assert.Equal(ExistingLocalAiState.Compatible, start.State);
        Assert.Equal(RemovalFixture.InstalledVersion, start.InstalledVersion);
        // The fixture writes no release record, so this machine cannot say which release
        // it is — the headline says what it knows, and the build id is named below it.
        Assert.Equal("LocalAi is installed on this computer.", start.Headline);
        Assert.Contains(
            "Build " + RemovalFixture.InstalledVersion,
            start.Detail,
            StringComparison.Ordinal);
        Assert.False(start.Option(StartChoice.Install).IsAvailable);
        Assert.Contains(
            "already installed",
            start.Option(StartChoice.Install).UnavailableReason,
            StringComparison.Ordinal);
        Assert.True(start.Option(StartChoice.UpdateOrRepair).IsAvailable);
        Assert.True(start.Option(StartChoice.CleanReinstall).IsAvailable);
        Assert.True(start.Option(StartChoice.Remove).IsAvailable);
    }

    [Fact]
    public void A_bare_machine_is_offered_the_install_and_told_why_not_the_rest()
    {
        Directory.Delete(machine.Runtime, recursive: true);

        var start = new InstallerStartViewModel(machine.LocalAppData);

        Assert.Equal(ExistingLocalAiState.Absent, start.State);
        Assert.True(start.Option(StartChoice.Install).IsAvailable);
        Assert.All(
            new[] { StartChoice.UpdateOrRepair, StartChoice.CleanReinstall, StartChoice.Remove },
            choice =>
            {
                var option = start.Option(choice);
                Assert.False(option.IsAvailable);
                Assert.Contains("Nothing is installed", option.UnavailableReason, StringComparison.Ordinal);
            });
    }

    /// <summary>
    /// A directory that is there but not a working installation cannot be installed over: the
    /// package installer refuses it. The ways out are a clean reinstall, which removes the
    /// binaries first, and removal; the reason it could not be read is on the page.
    /// </summary>
    [Fact]
    public void A_broken_installation_can_be_reinstalled_or_removed_but_not_installed_over()
    {
        Directory.Delete(
            Path.Combine(machine.Runtime, "bin", "versions", RemovalFixture.InstalledVersion),
            recursive: true);

        var start = new InstallerStartViewModel(machine.LocalAppData);

        Assert.Equal(ExistingLocalAiState.Unrecognized, start.State);
        Assert.True(start.HasProblem);
        Assert.Contains(
            $"Version '{RemovalFixture.InstalledVersion}' is missing.",
            start.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "A clean reinstall replaces the binaries",
            start.Detail,
            StringComparison.Ordinal);
        var repair = start.Option(StartChoice.UpdateOrRepair);
        Assert.Equal("Update or repair", repair.Title);
        Assert.False(repair.IsAvailable);
        Assert.Equal(
            "It is not a working installation, so nothing can be installed over it.",
            repair.UnavailableReason);
        Assert.True(start.Option(StartChoice.CleanReinstall).IsAvailable);
        Assert.True(start.Option(StartChoice.Remove).IsAvailable);
        Assert.False(start.Option(StartChoice.Install).IsAvailable);
    }

    /// <summary>
    /// A version missing a file is repaired, not reinstalled: "Update or repair" puts the file
    /// back from the verified release, as it does a file that differs.
    /// </summary>
    [Fact]
    public void A_version_missing_a_file_is_offered_the_repair()
    {
        File.Delete(Path.Combine(
            machine.Runtime,
            "bin",
            "versions",
            RemovalFixture.InstalledVersion,
            "codesearch.exe"));

        var start = new InstallerStartViewModel(machine.LocalAppData);

        Assert.Equal(ExistingLocalAiState.Compatible, start.State);
        Assert.False(start.HasProblem);
        Assert.Equal("LocalAi is installed on this computer.", start.Headline);
        Assert.Contains(
            "Build " + RemovalFixture.InstalledVersion,
            start.Detail,
            StringComparison.Ordinal);
        Assert.True(start.Option(StartChoice.UpdateOrRepair).IsAvailable);
        Assert.Equal(
            "LocalAi is already installed — choose “Update or repair”.",
            start.Option(StartChoice.Install).UnavailableReason);
        Assert.False(start.Option(StartChoice.Install).IsAvailable);
        Assert.True(start.Option(StartChoice.CleanReinstall).IsAvailable);
        Assert.True(start.Option(StartChoice.Remove).IsAvailable);
    }

    /// <summary>
    /// A clean reinstall is not a fifth mechanism: it is the reinstall-friendly row of the
    /// removal matrix, which keeps the indexes and settings, followed by an installation.
    /// </summary>
    [Theory]
    [InlineData(StartChoice.CleanReinstall, RemovalPreset.ReinstallFriendly)]
    [InlineData(StartChoice.Remove, RemovalPreset.FullUninstall)]
    public void Each_errand_that_removes_something_opens_on_its_own_preset(
        StartChoice choice,
        RemovalPreset expected) =>
        Assert.Equal(expected, InstallerStartViewModel.PresetFor(choice));

    [Fact]
    public void Every_option_says_what_it_does()
    {
        var start = new InstallerStartViewModel(machine.LocalAppData);

        Assert.Equal(4, start.Actions.Count);
        Assert.All(start.Actions, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Title));
            Assert.False(string.IsNullOrWhiteSpace(option.Description));
            Assert.True(option.IsAvailable || option.UnavailableReason.Length > 0);
        });
    }

    /// <summary>
    /// Apps &amp; features passes this back to the same executable, so it has to reach removal
    /// without going through the start page. The slash and single-dash spellings are accepted
    /// because a command a shell quotes slightly differently must not silently start an
    /// installer instead.
    /// </summary>
    [Theory]
    [InlineData("--uninstall")]
    [InlineData("/uninstall")]
    [InlineData("-UNINSTALL")]
    public void The_uninstall_argument_is_recognised(string argument) =>
        Assert.True(App.IsUninstallRequested([argument]));

    [Theory]
    [InlineData("--install")]
    [InlineData("uninstall")]
    [InlineData("--uninstall-everything")]
    public void Anything_else_starts_the_ordinary_way(string argument) =>
        Assert.False(App.IsUninstallRequested([argument]));

    [Fact]
    public void No_arguments_start_the_ordinary_way()
    {
        Assert.False(App.IsUninstallRequested([]));
        Assert.False(App.IsUninstallRequested(null));
    }

    /// <summary>
    /// The inspector is the only thing this page reads the machine with, so a probe that
    /// throws must not take the wizard down with it — it reports an unrecognised installation,
    /// which leaves a clean reinstall and removal available.
    /// </summary>
    [Fact]
    public void An_installation_that_cannot_be_read_is_still_something_to_act_on()
    {
        var start = new InstallerStartViewModel(
            machine.LocalAppData,
            new UnreadableInspector("the version directory could not be opened"));

        Assert.Equal(ExistingLocalAiState.Unrecognized, start.State);
        Assert.Contains("could not be opened", start.Detail, StringComparison.Ordinal);
        Assert.True(start.Option(StartChoice.Remove).IsAvailable);
    }

    private sealed class UnreadableInspector(string reason) : IExistingLocalAiInspector
    {
        public ExistingLocalAiSnapshot Inspect(string localAppData) =>
            new(ExistingLocalAiState.Unrecognized, null, null, reason);
    }
}
