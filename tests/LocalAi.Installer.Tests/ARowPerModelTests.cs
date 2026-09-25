using System.Xml.Linq;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Six models take tens of minutes, and the install page showed one sentence and one bar for all
/// of it. Which model the run was on, which were done, and which never got as far as being tried
/// were all in the log afterwards and nowhere while it mattered.
///
/// A row per model, each with a state of its own. The plan comes from the signed manifest, so
/// every row exists before the first download starts.
/// </summary>
public sealed class ARowPerModelTests
{
    [Fact]
    public void Every_model_gets_a_row_before_the_first_one_starts()
    {
        var wizard = new InstallerWizardViewModel();
        Assert.False(wizard.HasModelRuns);

        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Planned));

        Assert.True(wizard.HasModelRuns);
        Assert.Equal(["model-a", "model-b"], wizard.ModelRuns.Select(row => row.Tag));
        Assert.All(wizard.ModelRuns, row => Assert.Equal(ModelRunState.Waiting, row.State));
    }

    [Fact]
    public void A_step_reaches_the_row_it_names_and_leaves_the_others_alone()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Planned));

        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Checking));

        Assert.Equal(ModelRunState.Waiting, wizard.ModelRuns[0].State);
        Assert.Equal(ModelRunState.Working, wizard.ModelRuns[1].State);
    }

    [Theory]
    [InlineData(ModelProvisioningPhase.Accepted, ModelRunState.Ready, RunMark.Done, "ready")]
    [InlineData(ModelProvisioningPhase.Rejected, ModelRunState.Rejected, RunMark.Warned, "does not fit")]
    [InlineData(ModelProvisioningPhase.TrialLoadFailed, ModelRunState.Failed, RunMark.Failed, "trial load failed")]
    public void A_verdict_updates_the_row_and_its_bindings_before_the_batch_finishes(
        ModelProvisioningPhase verdict,
        ModelRunState expectedState,
        RunMark expectedMark,
        string expectedDetail)
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Checking));
        var first = wizard.ModelRuns[0];
        var changed = new List<string?>();
        first.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        wizard.ApplyModelStep(Step("model-a", verdict));
        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Checking));

        Assert.Equal(expectedState, first.State);
        Assert.Equal(expectedMark, first.Mark);
        Assert.Contains(expectedDetail, first.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(ModelRunRow.State), changed);
        Assert.Contains(nameof(ModelRunRow.Mark), changed);
        Assert.Contains(nameof(ModelRunRow.Detail), changed);
        Assert.Equal(ModelRunState.Working, wizard.ModelRuns[1].State);
        Assert.Equal(RunMark.Busy, wizard.ModelRuns[1].Mark);
    }

    [Theory]
    [InlineData(ModelProvisioningPhase.Downloading)]
    [InlineData(ModelProvisioningPhase.Verifying)]
    [InlineData(ModelProvisioningPhase.Storing)]
    [InlineData(ModelProvisioningPhase.Other)]
    [InlineData(ModelProvisioningPhase.Checking)]
    public void Finishing_a_download_does_not_claim_the_model_passed_validation(
        ModelProvisioningPhase phase)
    {
        var row = new ModelRunRow("model-a", 2048);

        row.Apply(Step("model-a", phase, completedBytes: 1024, totalBytes: 1024));

        Assert.Equal(ModelRunState.Working, row.State);
        Assert.Equal(RunMark.Busy, row.Mark);
    }

    [Fact]
    public void A_download_says_how_far_it_has_got()
    {
        var row = new ModelRunRow("model-a", 2048);

        row.Apply(Step(
            "model-a",
            ModelProvisioningPhase.Downloading,
            completedBytes: 5_046_586_573,
            totalBytes: 13_743_895_347));

        Assert.Equal(ModelRunState.Working, row.State);
        Assert.Contains("4.7", row.Detail, StringComparison.Ordinal);
        // The tag has a column of its own, so repeating it here would be the same word twice.
        Assert.DoesNotContain("model-a", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Ready" is not the whole news: a model already on the machine cost nothing, and one that
    /// was fetched explains where the last twenty minutes went.
    /// </summary>
    [Theory]
    [InlineData(true, "downloaded")]
    [InlineData(false, "already")]
    public void A_model_that_fits_says_whether_it_had_to_be_fetched(bool pulled, string expected)
    {
        var row = new ModelRunRow("model-a", 2048);

        row.Settled(BrokerModelInstallOutcome.Accepted, pulled, [], "accepted");

        Assert.Equal(ModelRunState.Ready, row.State);
        Assert.Contains(expected, row.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refusal_names_the_smaller_contexts_that_would_have_fitted()
    {
        var row = new ModelRunRow("model-a", 8192);

        row.Settled(
            BrokerModelInstallOutcome.RejectedResidency,
            downloaded: false,
            [new ModelFallbackSuggestion("model-a", 2048, 1024)],
            "residency_rejected");

        Assert.Equal(ModelRunState.Rejected, row.State);
        Assert.Contains("2048", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The code is a status token and travels untranslated: it is what a person quotes when they
    /// ask what went wrong.
    /// </summary>
    [Theory]
    [InlineData(BrokerModelInstallOutcome.Failed)]
    [InlineData(BrokerModelInstallOutcome.TimedOut)]
    public void A_failure_quotes_its_code(BrokerModelInstallOutcome outcome)
    {
        var row = new ModelRunRow("model-a", 2048);

        row.Settled(outcome, downloaded: false, [], "process_failure");

        Assert.Equal(ModelRunState.Failed, row.State);
        Assert.Contains("process_failure", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure stops the batch, so every model after it gets no result at all. That is a state
    /// of its own and it had no representation anywhere: those rows would otherwise sit spinning
    /// on a run that has finished.
    /// </summary>
    [Fact]
    public void A_model_the_run_never_reached_stops_waiting()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-b", ModelProvisioningPhase.Planned));
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Downloading));

        wizard.SettleModelRuns();

        Assert.Equal(ModelRunState.Unknown, wizard.ModelRuns[0].State);
        Assert.Equal(ModelRunState.NotReached, wizard.ModelRuns[1].State);
    }

    [Fact]
    public void A_row_that_already_has_a_verdict_keeps_it()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        wizard.ModelRuns[0].Settled(BrokerModelInstallOutcome.Accepted, true, [], "accepted");

        wizard.SettleModelRuns();

        Assert.Equal(ModelRunState.Ready, wizard.ModelRuns[0].State);
    }

    /// <summary>
    /// Every mark has something to draw. One added without a geometry draws nothing at all, which
    /// reads as a row that has stopped rather than as a row in a state nobody drew.
    /// </summary>
    [Fact]
    public void Every_mark_has_something_to_draw()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var drawn = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(presentation + "DataTrigger")
            .Where(trigger => trigger.Attribute("Binding")?.Value == "{Binding Mark}")
            .Select(trigger => trigger.Attribute("Value")?.Value ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            Enum.GetValues<RunMark>(),
            mark => Assert.Contains(
                $"{{x:Static vm:RunMark.{mark}}}",
                drawn,
                StringComparer.Ordinal));
    }

    /// <summary>
    /// The states a reader has to tell apart at a glance get different marks. Waiting and
    /// not-reached deliberately share one — both are a row nothing happened to, and the
    /// difference between them is what the detail says.
    /// </summary>
    [Fact]
    public void The_states_that_must_look_different_do()
    {
        var wizard = new InstallerWizardViewModel();
        wizard.ApplyModelStep(Step("model-a", ModelProvisioningPhase.Planned));
        var row = wizard.ModelRuns[0];

        var waiting = row.Mark;
        row.Apply(Step("model-a", ModelProvisioningPhase.Downloading));
        var working = row.Mark;
        row.Settled(BrokerModelInstallOutcome.Accepted, true, [], "accepted");
        var ready = row.Mark;
        row.Settled(BrokerModelInstallOutcome.RejectedResidency, false, [], "residency_rejected");
        var rejected = row.Mark;
        row.Settled(BrokerModelInstallOutcome.Failed, false, [], "process_failure");
        var failed = row.Mark;

        Assert.Equal(5, new[] { waiting, working, ready, rejected, failed }.Distinct().Count());
        row.NeverReached();
        Assert.Equal(waiting, row.Mark);
    }

    /// <summary>
    /// The batch's verdict replaces the phrase the run left on the row. For a trial load that
    /// failed it says the same thing again rather than a code; any other failure keeps its code.
    /// </summary>
    [Fact]
    public void A_failed_trial_load_keeps_its_words_when_the_batch_settles_it()
    {
        var trial = new ModelRunRow("model-a", 2048);
        var other = new ModelRunRow("model-b", 2048);

        trial.Settled(BrokerModelInstallOutcome.Failed, false, [], "trial_load_failed");
        other.Settled(BrokerModelInstallOutcome.Failed, false, [], "process_failure");

        Assert.Equal(ModelRunState.Failed, trial.State);
        Assert.Equal("trial load failed", trial.Detail);
        Assert.Equal("failed (process_failure)", other.Detail);
    }

    private static ModelProvisioningProgress Step(
        string model,
        ModelProvisioningPhase phase,
        long completedBytes = 0,
        long totalBytes = 0) =>
        new(model, 2048, phase, 0, 2, completedBytes, totalBytes);
}
