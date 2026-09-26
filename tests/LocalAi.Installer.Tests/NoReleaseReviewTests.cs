using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// What the review says about a run with no verified release, checked against what such a run
/// does: it installs the ticked prerequisites, stores the two settings into a runtime root that
/// already exists, and nothing else.
/// </summary>
public sealed class NoReleaseReviewTests : IDisposable
{
    private readonly string existingRoot = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.NoReleaseReview",
        Guid.NewGuid().ToString("N"));

    private readonly string missingRoot = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.NoReleaseReview",
        Guid.NewGuid().ToString("N"),
        "missing");

    public NoReleaseReviewTests() => Directory.CreateDirectory(existingRoot);

    public void Dispose()
    {
        if (Directory.Exists(existingRoot))
        {
            Directory.Delete(existingRoot, recursive: true);
        }
    }

    /// <summary>
    /// The settings are written whenever the runtime root exists, package or not, so a review
    /// that said "nothing will be applied" sat under "Update check: on (currently off — this run
    /// changes it)" on the same page. And prerequisites are named only when one is ticked: on an
    /// update nothing usually is (#264).
    /// </summary>
    [Theory]
    [InlineData(true, true,
        "The prerequisites above will still be installed, and the video memory and update " +
        "check settings stored.")]
    [InlineData(true, false, "Only the prerequisites above will be applied.")]
    [InlineData(false, true,
        "The video memory and update check settings will still be stored.")]
    [InlineData(false, false, "Nothing will be applied.")]
    public void The_warning_names_what_a_run_without_a_package_still_does(
        bool prerequisite,
        bool rootExists,
        string expected)
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair)
        {
            RuntimeRoot = rootExists ? existingRoot : missingRoot,
        };
        if (prerequisite)
        {
            var installable = wizard.Dependencies.Dependencies
                .First(dependency => dependency.IsInstallable);
            wizard.Dependencies.SetConsent(installable.Id, consent: true);
        }

        Assert.Contains(expected, wizard.ReviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warning_names_every_effect_that_needs_the_package()
    {
        var review = new InstallerWizardViewModel(StartChoice.Install).ReviewText;

        Assert.Contains(
            "Warning: no release has been verified, so LocalAi itself will not be installed, " +
            "and no models, client applications or PATH entry will be set up.",
            review,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// "Below" is true only while the release button is on the page, which is an update that has
    /// not opened the release step. Anywhere else the step is in the rail before this one.
    /// </summary>
    [Fact]
    public void An_install_is_sent_back_to_the_package_step()
    {
        var review = new InstallerWizardViewModel(StartChoice.Install).ReviewText;

        Assert.Contains(
            "Go back to the LocalAi package step to choose a release or a folder holding one.",
            review,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Choose a release below", review, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_with_the_release_folded_is_sent_to_the_button_below()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);
        Assert.True(wizard.IsReleaseFolded);

        Assert.Contains(
            "Choose a release below, or point the installer at a folder holding one, and " +
            "verify it before continuing.",
            wizard.ReviewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_that_opened_the_release_step_is_sent_back_to_it()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.UpdateOrRepair);
        wizard.RevealRelease();

        Assert.Contains(
            "Go back to the LocalAi package step",
            wizard.ReviewText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The PATH entry is added only after the package installs, so without one the line that
    /// promised it would be added described an effect that was not coming.
    /// </summary>
    [Fact]
    public void Without_a_package_the_path_is_left_alone()
    {
        var review = new InstallerWizardViewModel(StartChoice.Install).ReviewText;

        Assert.Contains(
            "PATH: unchanged, because LocalAi will not be installed.",
            review,
            StringComparison.Ordinal);
        Assert.DoesNotContain("will be added for this account", review, StringComparison.Ordinal);
    }
}
