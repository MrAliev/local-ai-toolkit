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
/// An update whose broker failed before the first model finished «Обновление не завершено» with
/// every row unmarked: «Локальные модели — не выполнялось», while its log said "Model setup
/// stopped early: ProcessFailure (broker_failure)." and its journal recorded the model step as
/// Completed. A batch that reaches no model leaves no row of its own, so the step says it.
/// </summary>
public sealed class ModelSetupThatFailedSaysSoTests
{
    [Fact]
    public void Setup_that_stopped_before_its_first_model_is_a_failure()
    {
        var row = RunResults.Models(
            [],
            new ModelStepFailure(ModelStepFailureKind.Stopped, "broker_failure"));

        Assert.Equal(RunMark.Failed, row.Mark);
        Assert.Equal("failed (broker_failure)", row.Detail);
    }

    [Fact]
    public void Models_asked_for_and_none_requested_is_a_failure()
    {
        var row = RunResults.Models(
            [],
            new ModelStepFailure(
                ModelStepFailureKind.NothingRequested,
                "The release manifest lists no models, so none can be installed from it."));

        Assert.Equal(RunMark.Failed, row.Mark);
        Assert.Equal("none set up: no model was requested. The log below says why.", row.Detail);
    }

    [Fact]
    public void Setup_cancelled_before_its_first_model_is_not_called_a_failure()
    {
        var row = RunResults.Models(
            [],
            new ModelStepFailure(ModelStepFailureKind.Cancelled, "cancelled"));

        Assert.Equal(RunMark.None, row.Mark);
        Assert.Equal("validation cancelled", row.Detail);
    }

    [Fact]
    public void A_step_nothing_went_wrong_in_stays_as_it_was()
    {
        Assert.Equal("not attempted", RunResults.Models([]).Detail);
        Assert.Null(ModelStepFailure.Of(BrokerModelBatchStopReason.None, "none"));
        Assert.Null(ModelStepFailure.Of(null));
    }

    [Theory]
    [InlineData(BrokerModelBatchStopReason.ProcessFailure, "broker_failure", ModelStepFailureKind.Stopped)]
    [InlineData(BrokerModelBatchStopReason.TimedOut, "active_version_lease_timeout", ModelStepFailureKind.Stopped)]
    [InlineData(BrokerModelBatchStopReason.ProtocolFailure, "invalid_status", ModelStepFailureKind.Stopped)]
    [InlineData(BrokerModelBatchStopReason.LauncherTrustFailure, "release_trust_failure", ModelStepFailureKind.Stopped)]
    [InlineData(BrokerModelBatchStopReason.Cancelled, "cancelled", ModelStepFailureKind.Cancelled)]
    public void A_batch_that_stopped_early_failed_the_step(
        BrokerModelBatchStopReason stopReason,
        string code,
        ModelStepFailureKind expected)
    {
        Assert.Equal(new ModelStepFailure(expected, code), ModelStepFailure.Of(stopReason, code));
    }

    [Fact]
    public void A_report_with_no_batch_says_nothing_was_requested_and_why()
    {
        var failure = ModelStepFailure.Of(new ReleaseModelInstallReport(
            ["The release manifest lists no models, so none can be installed from it."],
            null));

        Assert.Equal(
            new ModelStepFailure(
                ModelStepFailureKind.NothingRequested,
                "The release manifest lists no models, so none can be installed from it."),
            failure);
    }

    [Fact]
    public async Task The_journal_and_the_page_say_the_step_stopped()
    {
        using var logs = new TemporaryDirectory("LocalAi-model-step-stopped");
        var wizard = await RunEndingIn(logs, wizard =>
            wizard.CloseModelStep(
                wizard.BeginModelStep(),
                new ModelStepFailure(ModelStepFailureKind.Stopped, "broker_failure"),
                pulled: 0));

        var models = wizard.Finish.Results.Single(row => row.Title == PageLabels.LocalModels);
        Assert.Equal(RunMark.Failed, models.Mark);
        Assert.Contains("broker_failure", models.Detail, StringComparison.Ordinal);
        var step = ModelStep(logs);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
        Assert.Contains("broker_failure", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_requested_no_model_fails_its_step_in_the_journal()
    {
        using var logs = new TemporaryDirectory("LocalAi-model-step-none");
        var wizard = await RunEndingIn(logs, wizard =>
            wizard.FinishModelSetup(
                new StringBuilder(),
                new ReleaseModelInstallReport(
                    ["The release manifest lists no models, so none can be installed from it."],
                    null),
                new ModelProvisioningSelection(ModelProvisioningMode.Automatic),
                wizard.BeginModelStep()));

        Assert.Equal(
            RunMark.Failed,
            wizard.Finish.Results.Single(row => row.Title == PageLabels.LocalModels).Mark);
        var step = ModelStep(logs);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
        Assert.Contains("lists no models", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_that_finished_is_still_recorded_as_completed()
    {
        using var logs = new TemporaryDirectory("LocalAi-model-step-done");
        await RunEndingIn(logs, wizard =>
            wizard.CloseModelStep(wizard.BeginModelStep(), failure: null, pulled: 2));

        var step = ModelStep(logs);
        Assert.Equal(InstallerRunStepStatus.Completed, step.Status);
        Assert.Contains("Models newly downloaded: 2.", step.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A real run with its real journal, stopped by the environment check it makes first. The
    /// package install itself cannot run in a test, so the model step is driven from there.
    /// </summary>
    private static async Task<InstallerWizardViewModel> RunEndingIn(
        TemporaryDirectory logs,
        Action<InstallerWizardViewModel> modelStep)
    {
        InstallerWizardViewModel? wizard = null;
        wizard = new InstallerWizardViewModel(
            StartChoice.UpdateOrRepair,
            () => null,
            detectEnvironment: _ =>
            {
                wizard!.ApplyPackageResult(new ReleaseInstallResult(
                    LocalAiPackageInstallStatus.Installed, "0.2.1-test", null, logs.Path, null));
                modelStep(wizard);
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
        return wizard;
    }

    private static InstallerRunStep ModelStep(TemporaryDirectory logs) =>
        InstallerRunJournal.Load(Assert.Single(Directory.GetFiles(logs.Path, "journal-*.json")))
            .Snapshot.Steps.Single(step => step.Kind == InstallerRunEffectKind.ModelInstall);
}
