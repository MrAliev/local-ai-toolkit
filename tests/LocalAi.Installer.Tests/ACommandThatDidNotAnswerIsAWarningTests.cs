using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The detector reports a command it found but whose version probe failed as Failed, with the
/// path it found. The table said "Not found" for it, beside that path, and blocked nothing.
/// </summary>
public sealed class ACommandThatDidNotAnswerIsAWarningTests
{
    [Fact]
    public void A_found_command_that_did_not_report_a_version_is_a_warning()
    {
        var page = new DiagnosePageViewModel();

        page.Load(HermeticDiagnosis.Supported(git: new DependencySnapshot(
            "Git",
            DependencyState.Failed,
            @"C:\tools\git.exe",
            null,
            "Version probe timed out.")));

        var row = page.Checks.Single(check => check.Name == "Git");
        Assert.Equal(CheckStatus.Warning, row.Status);
        Assert.Equal("Warning", row.StatusText);
        Assert.Equal(
            @"Found at C:\tools\git.exe, but it did not report a version (Version probe timed " +
            "out.). Needed to index repositories.",
            row.Detail);
    }

    [Fact]
    public void The_warning_is_worded_in_russian_too()
    {
        var original = LocalAi.Installer.Core.InstallerCulture.Current;
        LocalAi.Installer.Core.InstallerCulture.Current = LocalAi.Installer.Core.InstallerLanguage.Russian;
        try
        {
            var page = new DiagnosePageViewModel();
            page.Load(HermeticDiagnosis.Supported(git: new DependencySnapshot(
                "Git",
                DependencyState.Failed,
                @"C:\tools\git.exe",
                null,
                "Version probe failed.")));

            Assert.Equal(
                @"Найден в C:\tools\git.exe, но не сообщил версию (Version probe failed.). " +
                "Нужен, чтобы индексировать репозитории.",
                page.Checks.Single(check => check.Name == "Git").Detail);
        }
        finally
        {
            LocalAi.Installer.Core.InstallerCulture.Current = original;
        }
    }

    [Fact]
    public void A_command_that_is_not_there_is_still_not_found()
    {
        var page = new DiagnosePageViewModel();

        page.Load(HermeticDiagnosis.Supported(git: new DependencySnapshot(
            "Git",
            DependencyState.NotFound,
            null,
            null,
            null)));

        var row = page.Checks.Single(check => check.Name == "Git");
        Assert.Equal(CheckStatus.Missing, row.Status);
        Assert.Equal("Needed to index repositories.", row.Detail);
    }
}
