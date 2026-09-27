using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Rolling back the RC19 → RC29 update reported «Apps &amp; features entry for LocalAi 0.2.1-RC29:
/// left in place» and, in the package line below it, «Apps &amp; features: shows 0.2.1-RC19 again».
/// Both were true — the entry and the uninstaller copy it runs stay, and the package step puts the
/// version back — but the version in the first read as the opposite of the second.
/// </summary>
public sealed class TheRollbackReportAgreesWithItselfTests
{
    [Fact]
    public async Task The_registration_step_names_what_stays_rather_than_a_version()
    {
        using var logs = new TemporaryDirectory("LocalAi-registration-step");

        await RunEndingIn(logs, wizard => wizard.BeginRegistrationStep());

        var step = InstallerRunJournal
            .Load(Assert.Single(Directory.GetFiles(logs.Path, "journal-*.json")))
            .Snapshot.Steps.Single(step => step.Kind == InstallerRunEffectKind.UninstallRegistration);
        Assert.Equal("Apps & features entry and the uninstaller copy it runs", step.Description);
    }

    /// <summary>
    /// A real run with its real journal, stopped by the environment check it makes first; the
    /// registration itself writes to this account's registry, so its step is driven from there.
    /// </summary>
    private static async Task RunEndingIn(
        TemporaryDirectory logs,
        Action<InstallerWizardViewModel> step)
    {
        InstallerWizardViewModel? wizard = null;
        wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ =>
            {
                step(wizard!);
                return Task.FromException<EnvironmentDiagnosis>(
                    new InvalidOperationException("the test stops the run here"));
            })
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
            RuntimeRoot = logs.Combine("no-runtime-root"),
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
    }
}
