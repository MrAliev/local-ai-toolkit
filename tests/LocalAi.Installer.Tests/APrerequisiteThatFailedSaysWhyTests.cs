using System.Text.Json;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Offline and without WinGet, Git and Ollama failed in milliseconds and the log said only
/// "Git: failed.", the journal "The install command did not succeed." The reason was worded and
/// then handed to a property nothing shows, and the run overwrote it with the log anyway.
/// </summary>
public sealed class APrerequisiteThatFailedSaysWhyTests
{
    [Fact]
    public async Task Without_winget_the_log_and_the_journal_say_why_git_was_not_installed()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-cause");
        var logs = root.Combine("logs");
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(
                git: Missing("git"),
                winGet: Missing("winget")),
            root,
            page =>
            {
                page.SetConsent("Git", true);
                page.SetConsent("Ollama", true);
            });

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause =
            "WinGet is unavailable; install Git manually from https://git-scm.com/install/windows.";
        Assert.Contains(cause, wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.Contains(
            cause,
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "install-*.log"))),
            StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite Git (Git.Git)"));
    }

    [Fact]
    public async Task Without_npm_the_log_and_the_journal_say_why_an_indexer_was_not_installed()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-npm");
        var logs = root.Combine("logs");
        var wizard = ConfirmedInstall(
            HermeticDiagnosis.Supported(npm: Missing("npm")),
            root,
            page =>
            {
                // Found by the check, so no WinGet is asked for.
                page.SetInstalled("Git", true);
                page.SetInstalled("Ollama", true);
                page.SetConsent("ScipTypeScript", true);
            });

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        const string cause = "npm is unavailable; install Node.js 20 before SCIP TypeScript.";
        Assert.Contains(cause, wizard.Finish.Progress, StringComparison.Ordinal);
        Assert.Equal(cause, StepDetail(logs, "Prerequisite SCIP TypeScript (@sourcegraph/scip-typescript)"));
    }

    private static DependencySnapshot Missing(string name) =>
        new(name, DependencyState.NotFound, null, null, null);

    /// <summary>An installation walked page by page, with nothing outside the test's directory.</summary>
    private static InstallerWizardViewModel ConfirmedInstall(
        EnvironmentDiagnosis diagnosis,
        TemporaryDirectory root,
        Action<DependenciesPageViewModel> choose)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            detectEnvironment: _ => Task.FromResult(diagnosis))
        {
            EnableDependencyActions = true,
            LogDirectory = root.Combine("logs"),
            RuntimeRoot = root.Combine("no-runtime-root"),
            ScratchRoot = root.Combine("scratch"),
        };
        // No catalogue means nothing to weigh, so the check makes no registry request.
        wizard.Models.CatalogModels.Clear();
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(supported: true);
        Assert.True(wizard.MoveNext());
        Assert.Equal(InstallerPage.Dependencies, wizard.CurrentPage);
        choose(wizard.Dependencies);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            if (wizard.CurrentPage == InstallerPage.Models)
            {
                wizard.Models.Mode = ModelSelectionMode.Skip;
            }

            Assert.True(wizard.MoveNext());
        }

        wizard.SetReviewConfirmed(true);
        return wizard;
    }

    private static string? StepDetail(string logs, string description)
    {
        using var journal = JsonDocument.Parse(
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "journal-*.json"))));
        var step = Assert.Single(
            journal.RootElement.GetProperty("Steps").EnumerateArray(),
            candidate => candidate.GetProperty("Description").GetString() == description);
        return step.GetProperty("Detail").GetString();
    }
}
