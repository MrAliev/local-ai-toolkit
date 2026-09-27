using System.Text;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The model setup a run records for the next update (#781) said nothing in the run log, so an
/// unexpected mode on that update had nothing to explain it; and rolling the run back left its
/// choice in place for the next update to offer.
/// </summary>
public sealed class TheModelSetupChoiceIsLoggedAndUndoneTests
{
    private const string Recorded = "Model setup choice recorded for the next update or repair: ";

    /// <summary>A write the record already holds is logged the same way: it is what the next update offers.</summary>
    [Theory]
    [InlineData(ModelSelectionMode.Skip)]
    [InlineData(ModelSelectionMode.Automatic)]
    [InlineData(ModelSelectionMode.ChooseExact)]
    public void Each_recorded_choice_has_its_line_in_the_log(ModelSelectionMode mode)
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-log");
        var wizard = InstalledOver(temp);
        var expected = Recorded + Choose(wizard, mode) + ".";

        var applied = new StringBuilder();
        wizard.RecordModelSetup(applied);
        var unchanged = new StringBuilder();
        wizard.RecordModelSetup(unchanged);

        Assert.Equal(expected, applied.ToString().Trim());
        Assert.Equal(expected, unchanged.ToString().Trim());
    }

    /// <summary>"Recorded" would be untrue for a run that recorded nothing.</summary>
    [Fact]
    public void Nothing_is_logged_when_nothing_was_recorded()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-log");
        Directory.CreateDirectory(Runtime(temp));
        var notInstalled = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = Runtime(temp),
        };
        var withoutRoot = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = temp.Combine("no-runtime-root"),
        };
        withoutRoot.ApplyPackageResult(Installed(temp.Path));

        var report = new StringBuilder();
        notInstalled.RecordModelSetup(report);
        withoutRoot.RecordModelSetup(report);

        Assert.Equal(string.Empty, report.ToString());
        Assert.Null(ModelSetupRecord.Read(Runtime(temp)));
        Assert.False(Directory.Exists(temp.Combine("no-runtime-root")));
    }

    /// <summary>
    /// A record that could not be written costs the next update its default and nothing else, so
    /// the finish page does not say an action failed.
    /// </summary>
    [Fact]
    public async Task A_record_that_could_not_be_written_is_logged_and_the_run_still_completed()
    {
        using var root = new TemporaryDirectory("LocalAi-model-setup-refused");
        // A directory where the file goes, so the write is refused.
        Directory.CreateDirectory(Path.Combine(root.Path, "settings", ModelSetupRecord.FileName));
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Path,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        wizard.ApplyPackageResult(Installed(root.Path));

        var report = new StringBuilder();
        wizard.RecordModelSetup(report);

        var line = report.ToString().Trim();
        Assert.StartsWith("Could not record the model setup choice (", line, StringComparison.Ordinal);
        Assert.EndsWith(
            "). The installation is unaffected; the next update or repair may not offer this choice.",
            line,
            StringComparison.Ordinal);
        Assert.Equal("All selected actions completed.", wizard.StepDescription);
        Assert.False(wizard.OpensTheLog);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rolling_back_the_run_puts_back_the_choice_it_replaced(bool recordedBefore)
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-rollback");
        Directory.CreateDirectory(Runtime(temp));
        var file = Path.Combine(Runtime(temp), "settings", ModelSetupRecord.FileName);
        if (recordedBefore)
        {
            ModelSetupRecord.Apply(Runtime(temp), new ModelProvisioningSelection(ModelProvisioningMode.Automatic));
        }

        var before = recordedBefore ? File.ReadAllBytes(file) : null;

        var wizard = await RunThatRecordsThenStops(temp, ModelSelectionMode.Skip);
        Assert.Equal(ModelProvisioningSelection.None, ModelSetupRecord.Read(Runtime(temp)));
        await wizard.RollbackThisRunAsync();

        if (before is not null)
        {
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.Contains($"{file}: restored the pre-install content.", wizard.Finish.RollbackReport, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(File.Exists(file));
            Assert.Contains($"{file}: removed.", wizard.Finish.RollbackReport, StringComparison.Ordinal);
        }
    }

    /// <summary>A choice the record already holds is not written, so there is nothing to take back.</summary>
    [Fact]
    public async Task A_choice_the_record_already_holds_is_not_journalled()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-rollback");
        Directory.CreateDirectory(Runtime(temp));
        ModelSetupRecord.Apply(Runtime(temp), ModelProvisioningSelection.None);

        var wizard = await RunThatRecordsThenStops(temp, ModelSelectionMode.Skip);

        var journal = InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(temp.Combine("logs"), "journal-*.json")));
        Assert.Empty(journal.Snapshot.Steps);
        Assert.False(wizard.CanRollback);
    }

    /// <summary>
    /// Repairing the release that is installed reports it installed, so a choice changed on a
    /// repair is recorded like one changed on an update.
    /// </summary>
    [Fact]
    public void A_repair_of_the_installed_release_records_the_choice()
    {
        using var temp = new TemporaryDirectory("LocalAi-model-setup-repair");
        Directory.CreateDirectory(Runtime(temp));
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = Runtime(temp),
        };
        wizard.Models.IsSkip = true;
        wizard.ApplyPackageResult(new ReleaseInstallResult(
            LocalAiPackageInstallStatus.AlreadyInstalled,
            "0.2.1-test",
            "0.2.1-test",
            temp.Path,
            null,
            RestoredFiles: ["localai.exe"]));

        wizard.RecordModelSetup(new StringBuilder());

        Assert.Equal(ModelProvisioningSelection.None, ModelSetupRecord.Read(Runtime(temp)));
    }

    private static string Runtime(TemporaryDirectory temp) => temp.Combine("runtime");

    private static ReleaseInstallResult Installed(string path) =>
        new(LocalAiPackageInstallStatus.Installed, "0.2.1-test", null, path, null);

    private static InstallerWizardViewModel InstalledOver(TemporaryDirectory temp)
    {
        Directory.CreateDirectory(Runtime(temp));
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = Runtime(temp),
        };
        wizard.ApplyPackageResult(Installed(temp.Path));
        return wizard;
    }

    /// <summary>Picks the mode on the page and returns how the log names it.</summary>
    private static string Choose(InstallerWizardViewModel wizard, ModelSelectionMode mode)
    {
        switch (mode)
        {
            case ModelSelectionMode.Skip:
                wizard.Models.IsSkip = true;
                return "skip";
            case ModelSelectionMode.Automatic:
                wizard.Models.IsAutomatic = true;
                return "automatic";
            default:
                var model = wizard.Models.CatalogModels[0];
                wizard.Models.IsChooseExact = true;
                wizard.Models.SelectedModel = model;
                wizard.Models.SelectedContext = model.ContextTokens[^1];
                return $"{model.Tag} with a {model.ContextTokens[^1]} token context";
        }
    }

    /// <summary>
    /// A real run with its real journal, stopped by the environment check it makes first. The
    /// package step would install a release, so the record is driven from there once the package
    /// is taken as installed.
    /// </summary>
    private static async Task<InstallerWizardViewModel> RunThatRecordsThenStops(
        TemporaryDirectory temp,
        ModelSelectionMode mode)
    {
        InstallerWizardViewModel? wizard = null;
        wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ =>
            {
                wizard!.ApplyPackageResult(Installed(temp.Path));
                wizard.RecordModelSetup(new StringBuilder());
                return Task.FromException<EnvironmentDiagnosis>(
                    new InvalidOperationException("the test stops the run here"));
            })
        {
            EnableDependencyActions = true,
            LogDirectory = temp.Combine("logs"),
            RuntimeRoot = Runtime(temp),
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.Models.Mode = mode;
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard;
    }
}
