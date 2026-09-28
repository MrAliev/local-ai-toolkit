using LocalAi.Installer.Core;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The install half of a clean reinstall opens after its removal half took the installed version
/// away, so there is nothing to update: RC31 sandbox run 20260928-153719-clean-reinstall showed
/// "Ready to update", an "Update" button and "Update complete" in a window titled "Reinstall".
/// </summary>
public sealed class AReinstallInstallsRatherThanUpdatesTests
{
    public AReinstallInstallsRatherThanUpdatesTests() => InstallerCulture.Current = InstallerLanguage.English;

    [Fact]
    public void Its_button_installs_and_its_window_still_says_reinstall()
    {
        var wizard = new InstallerWizardViewModel(StartChoice.CleanReinstall);

        Assert.Equal("Install", wizard.ActionText);
        Assert.Equal("LocalAi — Reinstall", wizard.WindowTitle);
    }

    [Theory]
    [InlineData(InstallerPage.Confirm, false, false, "Ready to install")]
    [InlineData(InstallerPage.Progress, false, false, "Installing")]
    [InlineData(InstallerPage.Finish, false, false, "Reinstall complete")]
    [InlineData(InstallerPage.Finish, true, false, "Installation not completed")]
    [InlineData(InstallerPage.Finish, false, true, "Installation cancelled")]
    public void Its_pages_say_install_and_its_success_says_the_reinstall_is_done(
        InstallerPage page, bool hasRunError, bool wasCancelled, string title)
    {
        Assert.Equal(title, WizardText.Title(page, isUpdate: false, hasRunError, wasCancelled, completesReinstall: true));
    }

    [Fact]
    public void Its_success_is_named_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal(
            "Переустановка завершена",
            WizardText.Title(InstallerPage.Finish, isUpdate: false, hasRunError: false, completesReinstall: true));
        Assert.Equal("Установить", new InstallerWizardViewModel(StartChoice.CleanReinstall).ActionText);
    }
}
