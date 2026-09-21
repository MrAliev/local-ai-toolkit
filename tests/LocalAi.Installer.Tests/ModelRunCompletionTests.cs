using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

public sealed class ModelRunCompletionTests
{
    [Theory]
    [InlineData(false, "already on this computer")]
    [InlineData(true, "downloaded")]
    public void The_batch_preserves_the_residency_warning_and_download_information(
        bool downloaded, string expectedDownloadDetail)
    {
        var row = new ModelRunRow("limited", 2048);

        row.Settled(BrokerModelInstallOutcome.Accepted, downloaded, [], "accepted", residencyDegraded: true);

        Assert.Equal(RunMark.Warned, row.Mark);
        Assert.Equal("ready with limitations — " + expectedDownloadDetail, row.Detail);
        Assert.Equal("1 of 1 ready; with limitations: 1", RunResults.Models([row]).Detail);
    }

    [Fact]
    public void A_model_accepted_with_limited_residency_warns_without_losing_its_readiness()
    {
        var limited = new ModelRunRow("limited", 2048);
        var ready = new ModelRunRow("ready", 2048);
        ready.Apply(Step("ready", ModelProvisioningPhase.Accepted));

        limited.Apply(Step("limited", ModelProvisioningPhase.Accepted) with { ResidencyDegraded = true });

        Assert.Equal(RunMark.Warned, limited.Mark);
        Assert.Equal("ready with limitations", limited.Detail);
        limited.Apply(Step("limited", ModelProvisioningPhase.Checking));
        Assert.Equal(RunMark.Warned, limited.Mark);
        var summary = RunResults.Models([ready, limited]);
        Assert.Equal(RunMark.Warned, summary.Mark);
        Assert.Equal("2 of 2 ready; with limitations: 1", summary.Detail);
    }

    [Theory]
    [InlineData(BrokerModelInstallOutcome.Accepted, ModelProvisioningPhase.Planned)]
    [InlineData(BrokerModelInstallOutcome.Accepted, ModelProvisioningPhase.Downloading)]
    [InlineData(BrokerModelInstallOutcome.Accepted, ModelProvisioningPhase.Checking)]
    [InlineData(BrokerModelInstallOutcome.RejectedResidency, ModelProvisioningPhase.Checking)]
    [InlineData(BrokerModelInstallOutcome.Failed, ModelProvisioningPhase.Checking)]
    public void Late_progress_keeps_the_verdict_and_its_explanation(
        BrokerModelInstallOutcome outcome,
        ModelProvisioningPhase latePhase)
    {
        var row = new ModelRunRow("model-a", 2048);
        row.Settled(outcome, true, [], "verdict");
        var expected = (row.State, row.Mark, row.Detail);

        row.Apply(Step("model-a", latePhase));

        Assert.Equal(expected, (row.State, row.Mark, row.Detail));
    }

    [Theory]
    [InlineData(ModelProvisioningPhase.Accepted)]
    [InlineData(ModelProvisioningPhase.Rejected)]
    public void A_verdict_received_during_the_batch_also_survives_late_progress(
        ModelProvisioningPhase verdict)
    {
        var row = new ModelRunRow("model-a", 2048);
        row.Apply(Step("model-a", verdict));
        var expected = (row.State, row.Mark, row.Detail);

        row.Apply(Step("model-a", ModelProvisioningPhase.Checking));

        Assert.Equal(expected, (row.State, row.Mark, row.Detail));
    }

    [Theory]
    [InlineData(false, ModelProvisioningPhase.Planned)]
    [InlineData(false, ModelProvisioningPhase.Checking)]
    [InlineData(true, ModelProvisioningPhase.Planned)]
    [InlineData(true, ModelProvisioningPhase.Checking)]
    public void An_unfinished_check_cannot_give_the_batch_a_success_mark(
        bool hasReadyModel,
        ModelProvisioningPhase phase)
    {
        var unfinished = new ModelRunRow("model-b", 2048);
        unfinished.Apply(Step("model-b", phase));
        var ready = new ModelRunRow("model-a", 2048);
        ready.Settled(BrokerModelInstallOutcome.Accepted, false, [], "accepted");

        var summary = RunResults.Models(hasReadyModel ? [ready, unfinished] : [unfinished]);

        Assert.NotEqual(RunMark.Done, summary.Mark);
    }

    [Fact]
    public void Context_is_part_of_the_identity_of_a_check()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned) with { ContextTokens = 8192 });

        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Accepted) with { ContextTokens = 8192 });

        Assert.Equal(ModelRunState.Waiting, wizard.ModelRuns[0].State);
        Assert.Equal(ModelRunState.Ready, wizard.ModelRuns[1].State);
    }

    [Fact]
    public void Repeating_a_planned_step_does_not_add_a_second_check()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Accepted));

        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));

        Assert.Equal(ModelRunState.Ready, Assert.Single(wizard.ModelRuns).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_missing_verdict_is_different_from_a_check_that_never_started(bool cancelled)
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("ready", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("ready", ModelProvisioningPhase.Accepted));
        wizard.ApplyModelStep(Step("active", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("active", ModelProvisioningPhase.Checking));
        wizard.ApplyModelStep(Step("waiting", ModelProvisioningPhase.Planned));

        wizard.SettleModelRuns(cancelled);

        Assert.Equal(ModelRunState.Ready, wizard.ModelRuns[0].State);
        Assert.Equal(cancelled ? ModelRunState.Cancelled : ModelRunState.Unknown, wizard.ModelRuns[1].State);
        Assert.Contains(cancelled ? "cancelled" : "unknown", wizard.ModelRuns[1].Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModelRunState.NotReached, wizard.ModelRuns[2].State);
        Assert.NotEqual(wizard.ModelRuns[1].State, wizard.ModelRuns[2].State);
        Assert.All(wizard.ModelRuns, row => Assert.NotEqual(RunMark.Busy, row.Mark));
        Assert.Equal(
            cancelled
                ? "1 of 3 ready; validation cancelled: 1; 1 not validated"
                : "1 of 3 ready; result unknown: 1; 1 not validated",
            RunResults.Models(wizard.ModelRuns).Detail);
    }

    [Fact]
    public void A_cancelled_check_is_not_reported_as_a_technical_failure()
    {
        var row = new ModelRunRow("model-a", 2048);

        row.Settled(BrokerModelInstallOutcome.Cancelled, false, [], "cancelled");

        Assert.NotEqual(ModelRunState.Failed, row.State);
        Assert.Equal(RunMark.None, row.Mark);
        Assert.Contains("cancelled", row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task The_wizard_closes_unfinished_rows_before_publishing_a_failed_or_cancelled_run(
        bool cancelled,
        bool hasModelRows)
    {
        using var logs = new TemporaryDirectory("LocalAi-model-run-finish");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InstallerPresentation(new InstallerPreferencesStore(logs.Combine("preferences")));
        InstallerWizardViewModel? wizard = null;
        wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair, detectEnvironment: token =>
        {
            if (hasModelRows)
            {
                wizard!.ApplyPackageResult(new ReleaseInstallResult(
                    LocalAiPackageInstallStatus.Installed, "0.2.1-test", null, logs.Path, null));
                wizard!.ApplyModelStep(Step("ready", ModelProvisioningPhase.Planned));
                wizard.ApplyModelStep(Step("ready", ModelProvisioningPhase.Accepted));
                wizard.ApplyModelStep(Step("active", ModelProvisioningPhase.Planned));
                wizard.ApplyModelStep(Step("active", ModelProvisioningPhase.Checking));
                wizard.ApplyModelStep(Step("waiting", ModelProvisioningPhase.Planned));
            }

            if (cancelled)
            {
                stop.Cancel();
                return Task.FromCanceled<EnvironmentDiagnosis>(token);
            }

            return Task.FromException<EnvironmentDiagnosis>(new InvalidOperationException("diagnostic failure"));
        }, presentation: presentation)
        {
            EnableDependencyActions = true,
            LogDirectory = logs.Path,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);
        Assert.True(wizard.CanRun);

        Assert.False(await wizard.RunAsync(stop.Token));

        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
        Assert.True(wizard.HasRunError);
        Assert.False(wizard.IsComplete);
        Assert.Equal(cancelled, wizard.IsCanceled);
        Assert.True(wizard.Finish.HasNextSteps);
        Assert.Contains(cancelled ? "Installation stopped" : "Installation did not complete",
            wizard.Finish.NextSteps!, StringComparison.Ordinal);
        Assert.NotEqual(RunMark.Done,
            wizard.Finish.Results.Single(row => row.Title == PageLabels.LocalModels).Mark);
        if (hasModelRows)
        {
            var package = wizard.Finish.Results.Single(row => row.Title == PageLabels.ResultPackage);
            Assert.Equal(RunMark.Done, package.Mark);
            Assert.Contains("0.2.1-test", package.Detail, StringComparison.Ordinal);
            Assert.Contains(wizard.Finish.Results, row => row.Title == "active");
            Assert.Equal(ModelRunState.Ready, wizard.ModelRuns[0].State);
            Assert.Equal(cancelled ? ModelRunState.Cancelled : ModelRunState.Unknown, wizard.ModelRuns[1].State);
            Assert.Equal(ModelRunState.NotReached, wizard.ModelRuns[2].State);
        }
        else
        {
            Assert.Empty(wizard.ModelRuns);
        }

        Assert.All(wizard.ModelRuns, row => Assert.NotEqual(RunMark.Busy, row.Mark));
        Assert.NotNull(wizard.Finish.ReportPath);
        Assert.True(File.Exists(wizard.Finish.ReportPath));
        var technicalLog = wizard.Finish.Progress;
        presentation.ChooseLanguage(InstallerLanguage.Russian);
        Assert.Contains(cancelled ? "Установка остановлена" : "Установка завершилась с проблемами",
            wizard.Finish.NextSteps!, StringComparison.Ordinal);
        Assert.Equal(technicalLog, wizard.Finish.Progress);
        Assert.Equal(technicalLog + Environment.NewLine, File.ReadAllText(wizard.Finish.ReportPath!));
    }

    [Fact]
    public async Task A_report_that_cannot_be_saved_leaves_copyable_diagnostics_and_no_file_link()
    {
        using var logs = new TemporaryDirectory("LocalAi-report-failure");
        var blocked = logs.Combine("not-a-directory");
        File.WriteAllText(blocked, "existing user file");
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            LogDirectory = blocked,
        };
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        wizard.SetReviewConfirmed(true);

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.False(wizard.Finish.HasReportPath);
        Assert.Contains("Could not save the report", wizard.Finish.NextSteps!, StringComparison.Ordinal);
        Assert.Contains("Copy the technical details", wizard.Finish.NextSteps!, StringComparison.Ordinal);
        Assert.Contains("Nothing was installed", wizard.Finish.Progress!, StringComparison.Ordinal);
        Assert.Equal("existing user file", File.ReadAllText(blocked));
    }

    private static ModelProvisioningProgress Step(string model, ModelProvisioningPhase phase) =>
        new(model, 2048, phase, 0, 3);
}
