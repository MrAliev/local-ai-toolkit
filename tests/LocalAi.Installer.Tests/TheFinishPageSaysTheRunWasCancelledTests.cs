using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// An update cancelled during the download ended on "Update not completed", was told to address
/// a cause there was none of, and read that changes already made stayed in place when nothing had
/// been applied (#613, run 20260927-040916-update-from-rc19).
/// </summary>
public sealed class TheFinishPageSaysTheRunWasCancelledTests : IDisposable
{
    private const string HowFarItGot = "The list below shows how far it got before you cancelled.";
    private const string StartOver = "To start over, close this window and run the installer again.";
    private const string NothingToRollBack = "Nothing this run applied can be rolled back by the installer.";

    private readonly TemporaryDirectory root = new("LocalAi-finish-cancelled");

    public void Dispose() => root.Dispose();

    /// <summary>
    /// A clean reinstall's install half installs: its removal half took the installed version
    /// away, so a cancel there cancels an installation, not an update (#614).
    /// </summary>
    [Theory]
    [InlineData(StartChoice.UpdateOrRepair, "Update cancelled", "Обновление отменено")]
    [InlineData(StartChoice.CleanReinstall, "Installation cancelled", "Установка отменена")]
    public async Task A_cancelled_update_says_it_was_cancelled(StartChoice mode, string title, string russianTitle)
    {
        var presentation = Presentation();
        var wizard = await RunEndedBy(mode, presentation, cancel: true);

        Assert.Equal(title, wizard.StepTitle);
        Assert.Equal(HowFarItGot, wizard.StepDescription);
        Assert.Equal(StartOver + "\n\n" + NothingToRollBack, wizard.Finish.NextSteps);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(russianTitle, wizard.StepTitle);
        Assert.Equal("Список ниже показывает, докуда дошёл запуск до отмены.", wizard.StepDescription);
        Assert.Equal(
            "Чтобы начать заново, закройте это окно и снова запустите установщик.\n\n" +
            "Ничего из применённого этим запуском установщик откатить не может.",
            wizard.Finish.NextSteps);
    }

    [Fact]
    public async Task A_cancelled_installation_says_it_was_cancelled()
    {
        var presentation = Presentation();
        var wizard = await RunEndedBy(StartChoice.Install, presentation, cancel: true);

        Assert.Equal("Installation cancelled", wizard.StepTitle);
        Assert.Equal(HowFarItGot, wizard.StepDescription);
        Assert.Equal(StartOver + "\n\n" + NothingToRollBack, wizard.Finish.NextSteps);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Установка отменена", wizard.StepTitle);
    }

    /// <summary>
    /// The rollback paragraph still follows a cancel; only its words for "nothing to roll back"
    /// changed, and the button's words did not.
    /// </summary>
    [Fact]
    public void A_cancelled_run_that_can_be_rolled_back_says_what_the_button_undoes()
    {
        var text = InstallerRunMessages.NextSteps(new InstallerRunMessageFacts(
            WasCancelled: true,
            HasRunError: true,
            NoValidatedModels: false,
            HasRollbackReport: false,
            CanRollback: true,
            ReportSaveFailure: null,
            ClientNextSteps: null,
            PathAdded: false));

        Assert.Equal(
            StartOver + "\n\nThe rollback button can undo recorded reversible changes only. " +
            "Shared dependencies and downloaded models are retained.",
            text);
    }

    /// <summary>A failed update opened with "Installation did not complete successfully."</summary>
    [Fact]
    public async Task A_failed_update_does_not_call_itself_an_installation()
    {
        var presentation = Presentation();
        var wizard = await RunEndedBy(StartChoice.UpdateOrRepair, presentation, cancel: false);

        Assert.Equal("Update not completed", wizard.StepTitle);
        Assert.Equal(
            "Completed and unfinished actions are listed above.\n\n" +
            "Keep the report or copy the technical details below. After addressing the reported " +
            "cause, close this window and run the installer again.\n\n" +
            NothingToRollBack,
            wizard.Finish.NextSteps);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(
            "Выполненные и незавершённые действия перечислены выше.\n\n" +
            "Сохраните отчёт или скопируйте технические сведения ниже. Устраните указанную " +
            "причину, закройте это окно и снова запустите установщик.\n\n" +
            "Ничего из применённого этим запуском установщик откатить не может.",
            wizard.Finish.NextSteps);
    }

    private InstallerPresentation Presentation() =>
        new(new InstallerPreferencesStore(root.Combine("preferences")));

    /// <summary>A run stopped at its first probe, before it applies anything.</summary>
    private async Task<InstallerWizardViewModel> RunEndedBy(
        StartChoice mode,
        InstallerPresentation presentation,
        bool cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wizard = new InstallerWizardViewModel(
            mode,
            () => null,
            detectEnvironment: token =>
            {
                if (cancel)
                {
                    stop.Cancel();
                    return Task.FromCanceled<EnvironmentDiagnosis>(token);
                }

                return Task.FromException<EnvironmentDiagnosis>(new InvalidOperationException("diagnostic failure"));
            },
            presentation: presentation)
        {
            EnableDependencyActions = true,
            LogDirectory = root.Combine("logs"),
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        foreach (var name in new[]
                 {
                     "Git", "Ollama", "GitHubCli", "DotNetSdk",
                     "NodeJs", "ScipTypeScript", "Python", "ScipPython",
                 })
        {
            wizard.Dependencies.SetInstalled(name, true);
        }

        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);

        Assert.False(await wizard.RunAsync(stop.Token));
        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
        Assert.Equal(cancel, wizard.IsCanceled);
        return wizard;
    }
}
