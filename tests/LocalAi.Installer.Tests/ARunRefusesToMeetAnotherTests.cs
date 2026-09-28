using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A removal and a repair that met left LocalAi installed with no Apps &amp; features entry and no
/// PATH entry, while the repair reported both in place (RC32 sandbox run
/// 20260928-172015-concurrency, cases F and H). The removal looked for another run only when its
/// window opened, and the install wizard never looked: both now look when their button is pressed.
/// </summary>
public sealed class ARunRefusesToMeetAnotherTests : IDisposable
{
    private const string AnotherRun = "Another LocalAi installer or uninstaller is running on this computer.";

    public ARunRefusesToMeetAnotherTests() => InstallerCulture.Current = InstallerLanguage.English;

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task Install_is_refused_while_another_run_holds_its_lock()
    {
        var logs = Path.Combine(machine.Root, "logs");
        var probed = false;
        var wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: token =>
            {
                probed = true;
                return Task.FromException<EnvironmentDiagnosis>(new InvalidOperationException("the run started"));
            })
        {
            EnableDependencyActions = true,
            LogDirectory = logs,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        foreach (var name in new[] { "Git", "Ollama", "GitHubCli", "DotNetSdk", "NodeJs", "ScipTypeScript", "Python", "ScipPython" })
        {
            wizard.Dependencies.SetInstalled(name, true);
        }

        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);
        using var other = InstallerRunJournal.Start(logs);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.False(probed);
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        Assert.False(wizard.Review.IsConfirmed);
        Assert.StartsWith(AnotherRun, wizard.Review.ChangedNotice, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(logs, "journal-*.json"));
    }

    [Fact]
    public async Task Uninstall_is_refused_while_another_run_holds_its_lock()
    {
        var logs = Path.Combine(machine.Root, "reports");
        var wizard = new UninstallWizardViewModel(RemovalPreset.FullUninstall, true,
            machine.Layout, machine.Home, new Exits(), machine.HooksPathReader)
        {
            LogDirectory = logs,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        var before = RemovalFixture.Snapshot(machine.Runtime);
        using var other = InstallerRunJournal.Start(logs);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        Assert.True(wizard.IsBlocked);
        Assert.StartsWith(AnotherRun, wizard.BlockingNotice, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(logs, "journal-*.json"));
    }

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }
}
