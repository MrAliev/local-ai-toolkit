using System.Xml.Linq;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The page a run ends on was two scroll boxes. The top one held four counters that read
/// "0. 0. 0. 0." on the ordinary run where nobody ticked a prerequisite, and clipped its own last
/// line mid-path; under it an English log. Nothing there answered the only question a person has
/// at that point — whether their assistant is going to work now, and whether anything has to be
/// restarted. The answer existed, in a log line nobody scrolled to.
///
/// A row per thing the run touched, then what to do next, then where the evidence is. The
/// counters move into the log, which is where a tally belongs.
/// </summary>
public sealed class TheFinishPageAnswersTheQuestionTests
{
    [Fact]
    public void Unfinished_prerequisites_are_not_reported_as_successful()
    {
        var row = RunResults.Prerequisites(requested: 3, installed: 1, failed: 0);
        Assert.Equal(RunMark.Warned, row.Mark);
        Assert.Contains("not completed: 2", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_registration_does_not_claim_that_models_and_clients_work()
    {
        var detail = RunResults.AppsAndFeatures(null, @"C:\LocalAi\uninstall.exe");
        Assert.DoesNotContain("installed and works", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_client_configuration_keeps_success_failure_and_unfinished_distinct()
    {
        var applied = RunResults.Client(new("Codex", ClientRunState.Applied));
        var failed = RunResults.Client(new("Claude Code", ClientRunState.Failed, "invalid_json"));
        var pending = RunResults.Client(new("pending", ClientRunState.NotAttempted));
        var interrupted = RunResults.Client(new("interrupted", ClientRunState.Configuring));

        Assert.Equal(RunMark.Done, applied.Mark);
        Assert.Equal(RunMark.Failed, failed.Mark);
        Assert.Contains("invalid_json", failed.Detail, StringComparison.Ordinal);
        Assert.Contains("retry setup", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(RunMark.None, pending.Mark);
        Assert.Contains("not attempted", pending.Detail, StringComparison.Ordinal);
        Assert.Equal(RunMark.Warned, interrupted.Mark);
        Assert.Contains("unknown", interrupted.Detail, StringComparison.Ordinal);
        Assert.Equal(RunMark.Warned, RunResults.Registration(RegistrationOutcome.Missing).Mark);
        Assert.Contains("Remove", RunResults.Registration(RegistrationOutcome.Failed).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_row_says_what_became_of_it()
    {
        Assert.Equal(
            RunMark.Done,
            RunResults.Package("Installed, version 0.2.1", installed: true).Mark);
        Assert.Equal(
            RunMark.Failed,
            RunResults.Package("not installed — the directory holds other files", false).Mark);
        // Never attempted is not a failure: the run stopped before it got here.
        Assert.Equal(RunMark.None, RunResults.Package(null, installed: false).Mark);
    }

    /// <summary>
    /// The counters were the page's whole content and read "0. 0. 0. 0." on the ordinary run.
    /// A run that was asked for no prerequisites says that, rather than counting to zero four
    /// times.
    /// </summary>
    [Fact]
    public void A_run_that_installed_no_prerequisites_says_so_rather_than_counting_to_zero()
    {
        var row = RunResults.Prerequisites(requested: 0, installed: 0, failed: 0);

        Assert.Equal(RunMark.None, row.Mark);
        Assert.DoesNotContain("0", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prerequisite_that_failed_marks_the_row()
    {
        Assert.Equal(
            RunMark.Done,
            RunResults.Prerequisites(requested: 2, installed: 2, failed: 0).Mark);
        Assert.Equal(
            RunMark.Failed,
            RunResults.Prerequisites(requested: 2, installed: 1, failed: 1).Mark);
    }

    /// <summary>
    /// The model rows have already said which model did what. This is the count, and its mark is
    /// the worst thing that happened to any of them.
    /// </summary>
    [Fact]
    public void The_model_row_counts_what_is_ready_and_marks_the_worst_of_them()
    {
        Assert.Equal(RunMark.None, RunResults.Models([]).Mark);
        Assert.Equal(RunMark.Done, RunResults.Models([Ready(), Ready()]).Mark);
        Assert.Equal(RunMark.Warned, RunResults.Models([Ready(), Refused()]).Mark);
        Assert.Equal(RunMark.Failed, RunResults.Models([Refused(), Broken()]).Mark);
        Assert.Contains("1", RunResults.Models([Ready(), Refused()]).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_row_names_the_clients_it_configured()
    {
        var row = RunResults.Clients(["Claude Code", "Codex"]);

        Assert.Equal(RunMark.Done, row.Mark);
        Assert.Contains("Codex", row.Detail, StringComparison.Ordinal);
        Assert.Equal(RunMark.None, RunResults.Clients([]).Mark);
    }

    /// <summary>
    /// The one question this page exists to answer. It was a log line — «Restart the client to
    /// pick it up.» — under eighty others, in a box that did not scroll.
    /// </summary>
    [Fact]
    public void What_to_do_next_names_the_clients_that_have_to_be_restarted()
    {
        var finish = new FinishPageViewModel();
        Assert.False(finish.HasNextSteps);

        finish.NextSteps = RunResults.NextSteps(["Claude Code", "Codex"]);

        Assert.True(finish.HasNextSteps);
        Assert.Contains("Claude Code", finish.NextSteps!, StringComparison.Ordinal);
        Assert.Contains("Codex", finish.NextSteps!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_configured_nothing_has_nothing_to_say_about_restarting()
    {
        Assert.Null(RunResults.NextSteps([]));
    }

    /// <summary>
    /// The path used to be concatenated onto the summary and clipped mid-word by a box 110 high.
    /// It is its own field, beside a button that opens the folder.
    /// </summary>
    [Fact]
    public void The_report_path_is_its_own_field()
    {
        var finish = new FinishPageViewModel();
        Assert.False(finish.HasReportPath);

        finish.ReportPath = @"C:\Users\someone\AppData\Local\LocalAi-installer-logs\install.log";

        Assert.True(finish.HasReportPath);
    }

    /// <summary>
    /// A run that went wrong opens its log; a run that went right folds it away. Nothing that
    /// failed is hidden either way, because every failure is also a row.
    /// </summary>
    [Fact]
    public void The_log_opens_itself_on_a_run_that_went_wrong()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var expander = XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"))
            .Descendants(presentation + "Expander")
            .Single(element => element.Attribute("Header")?.Value == "{vm:Label Log}");

        Assert.Equal(
            "{Binding HasRunError, Mode=OneWay}",
            expander.Attribute("IsExpanded")?.Value);
    }

    /// <summary>
    /// The rows are not only constructible, they are what a finished run leaves behind. Built by
    /// a builder nothing calls, they would be a page that stays empty.
    /// </summary>
    [Fact]
    public async Task A_finished_run_leaves_its_rows_on_the_page()
    {
        // The update path, because it reaches the review in one step: the settings pages are
        // folded, and what this is about is the page the run ends on.
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);
        // What the environment probe does when it finishes.
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        wizard.MoveNext();
        wizard.SetReviewConfirmed(true);
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                PageLabels.ResultPackage,
                PageLabels.ResultPrerequisites,
                PageLabels.LocalModels,
                PageLabels.ResultClients,
            ],
            wizard.Finish.Results.Select(row => row.Title));
    }

    private static ModelRunRow Ready()
    {
        var row = new ModelRunRow("model-a", 2048);
        row.Settled(BrokerModelInstallOutcome.Accepted, true, [], "accepted");
        return row;
    }

    private static ModelRunRow Refused()
    {
        var row = new ModelRunRow("model-b", 2048);
        row.Settled(BrokerModelInstallOutcome.RejectedResidency, false, [], "residency_rejected");
        return row;
    }

    private static ModelRunRow Broken()
    {
        var row = new ModelRunRow("model-c", 2048);
        row.Settled(BrokerModelInstallOutcome.Failed, false, [], "process_failure");
        return row;
    }
}
