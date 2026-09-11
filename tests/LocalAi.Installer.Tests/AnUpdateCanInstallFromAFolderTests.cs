using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// An update folds the package page away, and that page is where the box for installing from a
/// folder lives. A machine with no route to GitHub therefore had nowhere to go: the release
/// could not resolve, and the one control that would have rescued the run was behind a button
/// named «Выбрать релиз…» on a later page — which is not what somebody in that situation is
/// looking for.
///
/// A failed resolve is precisely the run that needs the folder, so the page comes back on its
/// own rather than being explained.
/// </summary>
public sealed class AnUpdateCanInstallFromAFolderTests
{
    [Fact]
    public void The_button_names_the_folder_as_well_as_the_release()
    {
        // Both routes, because only one of them was named and it was the one that does not work
        // on a machine that cannot reach the feed.
        Assert.Contains("folder", PageLabels.ChooseARelease, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The picker's title names the three files a release folder has to hold, so somebody
    /// standing in the wrong directory can tell.
    /// </summary>
    [Fact]
    public void The_picker_says_what_a_release_folder_contains()
    {
        foreach (var asset in new[]
                 {
                     "release-manifest.json",
                     "release-manifest.sig",
                     "localai-package.zip",
                 })
        {
            Assert.Contains(asset, PageLabels.BrowseForFolderTitle, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The warning on the review page has to offer the folder too. It is the sentence somebody
    /// reads at the moment they discover nothing will be installed.
    /// </summary>
    [Fact]
    public void The_review_warning_offers_the_folder()
    {
        using (TestCulture.Reading("en"))
        {
            Assert.Contains(
                "folder",
                new InstallerWizardViewModel().ReviewText ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
