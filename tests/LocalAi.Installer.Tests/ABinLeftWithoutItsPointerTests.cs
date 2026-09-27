using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A removal whose deletion of bin failed partway can leave bin without its pointer (#614).
/// An installation refuses to adopt a bin it did not create, so the start screen offers what
/// a broken installation gets: a clean reinstall, which takes the binaries first, and removal. Both are
/// run here through the planner and the runner, not only offered.
/// </summary>
public sealed class ABinLeftWithoutItsPointerTests : IDisposable
{
    private const string NotInstalled =
        "LocalAi is not installed here; these rows apply to what is left of it, and the next " +
        "page lists every path found.";

    private const string Usual =
        "Start from one of the three presets and change any row you like. Nothing is removed " +
        "until you confirm it on the next page.";

    private readonly RemovalFixture machine = new();

    public ABinLeftWithoutItsPointerTests() =>
        File.Delete(Path.Combine(Bin, "current.json"));

    public void Dispose() => machine.Dispose();

    private string Bin => Path.Combine(machine.Runtime, "bin");

    [Fact]
    public void A_clean_reinstall_is_offered_rather_than_an_install()
    {
        var start = Start();

        Assert.Equal(ExistingLocalAiState.Leftovers, start.State);
        Assert.Equal(
            "LocalAi is not installed, but some of its files are still on this computer.",
            start.Headline);
        Assert.Equal(
            "A clean reinstall removes the binaries that are left and installs again; removing " +
            "takes everything that is left off this computer. Nothing is changed until you " +
            "confirm it on a review page.",
            start.Detail);
        var install = start.Option(StartChoice.Install);
        Assert.False(install.IsAvailable);
        Assert.Equal(
            "There is already a LocalAi directory here — choose “Clean reinstall”, which " +
            "removes its binaries first.",
            install.UnavailableReason);
        var repair = start.Option(StartChoice.UpdateOrRepair);
        Assert.False(repair.IsAvailable);
        Assert.Equal("Nothing is installed to update.", repair.UnavailableReason);
        var reinstall = start.Option(StartChoice.CleanReinstall);
        Assert.True(reinstall.IsAvailable);
        Assert.Equal(
            "Removes the binaries and the transient state, keeps the repository indexes and " +
            "the settings a fresh install would honour, then installs again.",
            reinstall.Description);
        var remove = start.Option(StartChoice.Remove);
        Assert.True(remove.IsAvailable);
        Assert.Equal(
            "Choose what goes and what stays, then take it off this computer.",
            remove.Description);
        Assert.Null(start.Selected);
        Assert.All(start.Actions, option => Assert.False(option.IsSelected, option.Title));
        Assert.False(start.HasProblem);
    }

    [Fact]
    public void The_same_reads_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        var start = Start();

        Assert.Equal(
            "LocalAi не установлен, но часть его файлов ещё на этом компьютере.",
            start.Headline);
        Assert.Equal(
            "Чистая переустановка удалит оставшиеся бинарные файлы и установит заново; " +
            "удаление уберёт с этого компьютера всё, что осталось. Ничего не изменится, пока " +
            "вы это не подтвердите на итоговой странице.",
            start.Detail);
        Assert.Equal(
            "Каталог LocalAi здесь уже есть — выберите «Чистая переустановка»: этот вариант " +
            "сначала удаляет его бинарные файлы.",
            start.Option(StartChoice.Install).UnavailableReason);
        Assert.Equal(
            "Нечего обновлять — ничего не установлено.",
            start.Option(StartChoice.UpdateOrRepair).UnavailableReason);
    }

    /// <summary>
    /// Without a pointer the runner still asks the launcher to stop, and the launcher answers
    /// that there is nothing to stop; the files go after it, bin included.
    /// </summary>
    [Fact]
    public async Task Removing_goes_through_the_planner_and_the_runner()
    {
        var token = TestContext.Current.CancellationToken;
        var start = Start();
        start.Select(StartChoice.Remove);
        Assert.Equal(StartChoice.Remove, start.Selected);
        var launcher = new LauncherWithoutPointer();
        var wizard = await Wizard(StartChoice.Remove, launcher);

        Assert.False(wizard.IsBlocked);
        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);
        Assert.True(await wizard.MoveNextAsync(token));
        Assert.Contains(Bin, wizard.PreviewText, StringComparison.Ordinal);
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(token));

        Assert.Equal("Removal complete", wizard.StepTitle);
        Assert.Equal(["stop"], Assert.Single(launcher.Calls));
        Assert.Equal(
            [RemovalMatrix.SigningKeyDirectoryName],
            Directory.EnumerateFileSystemEntries(machine.Runtime).Select(Path.GetFileName));
        Assert.Equal(ExistingLocalAiState.SigningKeysOnly, Inspector().Inspect(machine.LocalAppData).State);
    }

    /// <summary>
    /// The removal half takes bin and keeps what a reinstall honours; what it leaves is an
    /// ordinary first installation, which the package installer accepts.
    /// </summary>
    [Fact]
    public async Task A_clean_reinstall_takes_the_bin_through_the_planner_and_the_runner()
    {
        var token = TestContext.Current.CancellationToken;
        var start = Start();
        start.Select(StartChoice.CleanReinstall);
        Assert.Equal(StartChoice.CleanReinstall, start.Selected);
        var launcher = new LauncherWithoutPointer();
        var wizard = await Wizard(StartChoice.CleanReinstall, launcher);

        Assert.Equal(RemovalPreset.ReinstallFriendly, wizard.SelectedPreset);
        Assert.Equal(NotInstalled + " " + Usual, wizard.StepDescription);
        Assert.True(await wizard.MoveNextAsync(token));
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(token));

        Assert.True(wizard.CanContinueToInstall);
        Assert.Equal(["stop"], Assert.Single(launcher.Calls));
        Assert.False(Directory.Exists(Bin));
        Assert.All(RemovalMatrix.SettingsFileNames, name =>
            Assert.True(File.Exists(Path.Combine(machine.Runtime, name)), name));
        var after = Inspector().Inspect(machine.LocalAppData);
        Assert.Equal(ExistingLocalAiState.Leftovers, after.State);
        Assert.False(after.BinWithoutPointer);
    }

    private ExistingLocalAiInspector Inspector() => new(new SystemFileSystemProbe(), () => null);

    private InstallerStartViewModel Start() =>
        new(machine.LocalAppData, Inspector(), () => new InstalledVersion(null, null));

    /// <summary>The window the start screen opens for <paramref name="choice"/>, on this machine.</summary>
    private async Task<UninstallWizardViewModel> Wizard(StartChoice choice, IProcessRunner launcher)
    {
        var wizard = new UninstallWizardViewModel(
            InstallerStartViewModel.PresetFor(choice),
            offersInstallAfterwards: choice == StartChoice.CleanReinstall,
            machine.Layout,
            machine.Home,
            launcher,
            machine.HooksPathReader,
            inspector: Inspector())
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    /// <summary>
    /// What the launcher answers to <c>stop</c> with no pointer: its version stopper takes a
    /// missing pointer for nothing to stop, and exits with 0.
    /// </summary>
    private sealed class LauncherWithoutPointer : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add([.. arguments]);
            return Task.FromResult(new ProcessResult(0, "", "", false, false));
        }
    }
}
