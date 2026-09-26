using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// What an update stops asking, and what it must still show.
///
/// An update re-asked four questions it already had answers to — prerequisites that are
/// installed, models that are pulled, a residency policy now read from disk, client
/// registrations that exist — and then told the reader it was on "Step 3 of 9" of a nine-page
/// install (#257). The pages fold away; their values still reach the review page, because a
/// folded page must not become an unlisted effect.
/// </summary>
public sealed class UpdatePathFoldsSettingsTests
{
    [Fact]
    public void An_install_visits_every_page()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install);

        Assert.False(wizard.AreSettingsFolded);
        // Video memory merged into the models page, so eight rather than nine.
        Assert.Equal(8, wizard.StepList.Count);
    }

    [Fact]
    public void An_update_folds_the_four_questions_it_has_answers_to()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        Assert.True(wizard.AreSettingsFolded);
        // Diagnose, Confirm, Progress, Finish. The release follows from the errand, so the
        // package page is folded with the rest and resolved while the check runs.
        Assert.Equal(4, wizard.StepList.Count);
        Assert.Contains("Step 1 of 4", wizard.StepStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnose stays on every path: it is where an interrupted run is offered back and where
    /// an unsupported machine is stopped, neither of which an update may skip.
    /// </summary>
    [Fact]
    public void The_first_page_is_the_system_check_on_both_paths()
    {
        Assert.Equal(
            InstallerPage.Diagnose,
            new InstallerWizardViewModel(StartChoice.UpdateOrRepair).CurrentPage);
    }

    [Fact]
    public void Next_on_an_update_goes_from_the_check_straight_to_the_review()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);

        Assert.True(wizard.MoveNext());

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
    }

    [Fact]
    public void Back_from_the_review_returns_to_the_check_rather_than_a_folded_page()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.MoveNext();

        Assert.True(wizard.MovePrevious());

        Assert.Equal(InstallerPage.Diagnose, wizard.CurrentPage);
    }

    /// <summary>
    /// Folding is never irrevocable. The run where a carried-forward answer is wrong is
    /// exactly the run somebody discovers it on the review page.
    /// </summary>
    [Fact]
    public void The_settings_pages_come_back_on_request()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        wizard.RevealSettings();

        Assert.False(wizard.AreSettingsFolded);
        // The settings pages return; the release page is a separate question.
        Assert.Equal(7, wizard.StepList.Count);
        Assert.Equal(InstallerPage.Dependencies, wizard.CurrentPage);
    }

    /// <summary>
    /// The release and the settings are different questions, so one button for both would be
    /// the confusion this redesign removes. Choosing a release reveals that page alone.
    /// </summary>
    [Fact]
    public void The_release_page_comes_back_on_its_own()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        wizard.RevealRelease();

        Assert.False(wizard.IsReleaseFolded);
        Assert.True(wizard.AreSettingsFolded);
        Assert.Equal(5, wizard.StepList.Count);
        Assert.Equal(InstallerPage.Package, wizard.CurrentPage);
    }

    /// <summary>
    /// It was opened from the review for one answer, so Back returns there rather than to
    /// whatever precedes it in the rail.
    /// </summary>
    [Fact]
    public void Back_from_a_release_page_opened_off_the_review_returns_to_the_review()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.RevealRelease();

        Assert.True(wizard.MovePrevious());

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
    }

    [Fact]
    public void Revealing_twice_changes_nothing_further()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.RevealSettings();
        wizard.MoveNext();
        var page = wizard.CurrentPage;

        wizard.RevealSettings();

        Assert.Equal(page, wizard.CurrentPage);
    }

    /// <summary>
    /// The review page is the contract: folding a page must not remove its line, or the
    /// wizard would be applying an effect it never listed.
    /// </summary>
    [Theory]
    [InlineData("LocalAi package:")]
    [InlineData("Dependencies:")]
    [InlineData("Models:")]
    [InlineData("Video memory:")]
    [InlineData("Update check:")]
    public void Every_folded_page_still_reports_itself_on_the_review(string expected)
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);

        Assert.Contains(expected, wizard.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first page is called the same thing on both paths, because it is the same page: the
    /// probe runs unconditionally and the table renders as soon as it finishes. It was renamed
    /// to "Preparing" on the update path on the belief that the page there was one line and a
    /// spinner — a difference with nothing behind it, and this test pinned it.
    /// </summary>
    [Fact]
    public void The_first_step_is_called_the_same_thing_on_both_paths()
    {
        var update = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);
        var install = new InstallerWizardViewModel(StartChoice.Install);

        Assert.Equal("System check", update.StepTitle);
        Assert.Equal("System check", update.StepList[0].Title);
        Assert.Equal("System check", install.StepTitle);
        Assert.Equal("System check", install.StepList[0].Title);
        Assert.Equal(install.StepDescription, update.StepDescription);
    }

    /// <summary>
    /// The install lead stopped being true the moment results appeared, so it changes when
    /// they do.
    /// </summary>
    [Fact]
    public void The_install_lead_stops_describing_a_wait_once_there_is_a_table()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.Install);
        Assert.Contains("Checking this computer", wizard.StepDescription, StringComparison.Ordinal);

        wizard.Diagnose.IsChecking = false;

        Assert.Contains("What was found", wizard.StepDescription, StringComparison.Ordinal);
    }

    /// <summary>
    /// Back from the review of an update lands on the system check, where nothing can be
    /// changed; the folded pages come back through the buttons under the list.
    /// </summary>
    [Fact]
    public void A_folded_review_names_the_buttons_rather_than_back()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        wizard.MoveNext();
        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);

        Assert.Equal(
            "Review what is about to happen. To change anything, use the buttons under the " +
            "list; to apply it, click Update.",
            wizard.StepDescription);
    }

    [Fact]
    public void An_unfolded_review_names_back()
    {
        var wizard = Checked(StartChoice.Install);
        MarkEverythingInstalled(wizard);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext());
        }

        Assert.Equal(
            "Review what is about to happen. To change anything, go back; to apply it, click " +
            "Install.",
            wizard.StepDescription);
    }

    [Fact]
    public void A_review_with_one_group_folded_names_both_routes()
    {
        var wizard = Checked(StartChoice.UpdateOrRepair);
        MarkEverythingInstalled(wizard);
        wizard.RevealSettings();
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext());
        }

        Assert.Equal(
            "Review what is about to happen. To change anything, go back or use the button " +
            "under the list; to apply it, click Update.",
            wizard.StepDescription);
    }

    private static void MarkEverythingInstalled(InstallerWizardViewModel wizard)
    {
        foreach (var dependency in wizard.Dependencies.Dependencies)
        {
            wizard.Dependencies.SetInstalled(dependency.Id, true);
        }
    }

    /// <summary>
    /// A wizard whose system check has passed, which is what gates Next on the first page.
    /// Without it every navigation assertion here would be testing the gate rather than the
    /// folding.
    /// </summary>
    private static InstallerWizardViewModel Checked(StartChoice mode)
    {
        var wizard = new InstallerWizardViewModel(mode);
        // Both halves of the gate: the probe has finished, and it found a usable machine.
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        return wizard;
    }
}
