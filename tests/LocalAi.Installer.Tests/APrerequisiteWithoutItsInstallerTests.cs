using System.Xml.Linq;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Without WinGet the page still made Git and Ollama required ticks, Next waited for them, and
/// the run failed both in milliseconds. A component the installer cannot install is not offered:
/// its box is off, the row says why and where to get it, and a required one stops holding Next —
/// the package installs without them.
/// </summary>
public sealed class APrerequisiteWithoutItsInstallerTests
{
    private static readonly string[] InstalledByWinGet =
        ["Git", "Ollama", "GitHubCli", "DotNetSdk", "NodeJs", "Python"];

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Without_winget_its_components_are_not_offered_and_do_not_hold_next()
    {
        var page = new DependenciesPageViewModel();
        page.SetInstallers(winGetFound: false, npmFound: true);

        foreach (var id in InstalledByWinGet)
        {
            page.SetConsent(id, true);
            Assert.False(Row(page, id).IsInstallable);
            Assert.False(Row(page, id).IsConsented);
        }

        Assert.True(page.CanContinue);
        Assert.True(Row(page, "ScipTypeScript").IsInstallable);
        Assert.True(Row(page, "Git").HasAutomatedInstaller);
        Assert.Equal(
            "WinGet was not found, so this installer cannot install it. After installing it " +
            "yourself, check again. Download: https://git-scm.com/install/windows",
            Row(page, "Git").InstallerHint);
        Assert.True(Row(page, "Git").HasInstallerHint);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "WinGet не найден, поэтому установить автоматически не получится. Установите вручную " +
            "и проверьте снова. Скачать: https://ollama.com/download/windows",
            Row(page, "Ollama").InstallerHint);
    }

    /// <summary>"After installing it yourself" is advice for something that is missing.</summary>
    [Fact]
    public void A_component_already_here_is_not_told_to_be_installed_by_hand()
    {
        var page = new DependenciesPageViewModel();
        page.SetInstalled("Git", true);
        page.SetInstallers(winGetFound: false, npmFound: true);

        Assert.False(Row(page, "Git").IsInstallable);
        Assert.False(Row(page, "Git").HasInstallerHint);
    }

    [Fact]
    public void A_tick_on_a_component_that_can_no_longer_be_installed_is_taken_off()
    {
        var page = new DependenciesPageViewModel();
        page.SetConsent("Git", true);
        Assert.True(Row(page, "Git").IsConsented);

        page.SetInstallers(winGetFound: false, npmFound: true);

        Assert.False(Row(page, "Git").IsConsented);
        Assert.DoesNotContain("Git (install)", page.ReviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run installs Node.js first and checks the machine again after every component, so an
    /// indexer ticked beside Node.js 20 finds npm by the time its turn comes.
    /// </summary>
    [Fact]
    public void Without_npm_the_indexers_wait_for_nodejs_ticked_in_the_same_run()
    {
        var page = new DependenciesPageViewModel();
        page.SetInstallers(winGetFound: true, npmFound: false);

        foreach (var id in new[] { "ScipTypeScript", "ScipPython" })
        {
            Assert.False(Row(page, id).IsInstallable);
            Assert.Equal(
                "npm was not found; it comes with Node.js 20. With Node.js 20 installed, check again.",
                Row(page, id).InstallerHint);
        }

        page.SetConsent("NodeJs", true);
        page.SetConsent("ScipTypeScript", true);
        Assert.True(Row(page, "ScipPython").IsInstallable);
        Assert.True(Row(page, "ScipTypeScript").IsConsented);

        page.SetConsent("NodeJs", false);
        Assert.False(Row(page, "ScipTypeScript").IsInstallable);
        Assert.False(Row(page, "ScipTypeScript").IsConsented);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "npm не найден; он входит в Node.js 20. Когда Node.js 20 будет установлен, проверьте снова.",
            Row(page, "ScipPython").InstallerHint);
    }

    /// <summary>Node.js was ticked, and the check run again then found no WinGet to install it.</summary>
    [Fact]
    public void Without_winget_nodejs_cannot_bring_npm_either()
    {
        var page = new DependenciesPageViewModel();
        page.SetInstallers(winGetFound: true, npmFound: false);
        page.SetConsent("NodeJs", true);
        page.SetConsent("ScipTypeScript", true);

        page.SetInstallers(winGetFound: false, npmFound: false);

        Assert.False(Row(page, "NodeJs").IsConsented);
        Assert.False(Row(page, "ScipTypeScript").IsInstallable);
        Assert.False(Row(page, "ScipTypeScript").IsConsented);
    }

    [Fact]
    public void Check_again_is_offered_while_something_missing_cannot_be_installed()
    {
        var page = new DependenciesPageViewModel();
        Assert.False(page.NeedsCheckAgain);

        page.SetInstallers(winGetFound: false, npmFound: true);
        Assert.True(page.NeedsCheckAgain);

        foreach (var id in InstalledByWinGet)
        {
            page.SetInstalled(id, true);
        }

        Assert.False(page.NeedsCheckAgain);
    }

    [Fact]
    public void The_review_names_what_is_still_needed_by_hand()
    {
        var page = new DependenciesPageViewModel();
        page.SetInstallers(winGetFound: false, npmFound: true);
        Assert.Equal(
            "Dependencies: nothing selected; still needed, install by hand: Git, Ollama",
            page.ReviewText);

        page.SetConsent("ScipTypeScript", true);
        Assert.Equal(
            "Dependencies: SCIP TypeScript (install); still needed, install by hand: Git, Ollama",
            page.ReviewText);

        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Компоненты: SCIP TypeScript (установка); ещё нужны, установить вручную: Git, Ollama",
            page.ReviewText);
    }

    /// <summary>
    /// Git installed by hand stayed "Not installed" to the end of the wizard: the check ran once,
    /// on the first page. The button runs it again, and the page follows what it finds.
    /// </summary>
    [Fact]
    public async Task Check_again_runs_the_check_and_the_page_follows_it()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-check-again");
        var checks = 0;
        var wizard = Wizard(root, () => ++checks == 1
            ? HermeticDiagnosis.Supported(git: Missing("git"), winGet: Missing("winget"))
            : HermeticDiagnosis.Supported(git: Missing("git")));
        await wizard.CheckPrerequisitesAgainAsync();
        Assert.False(Row(wizard.Dependencies, "Git").IsInstallable);
        Assert.True(wizard.Dependencies.NeedsCheckAgain);
        Assert.True(wizard.CheckPrerequisitesAgainCommand.CanExecute(null));

        await wizard.CheckPrerequisitesAgainAsync();

        Assert.Equal(2, checks);
        Assert.True(Row(wizard.Dependencies, "Git").IsInstallable);
        Assert.False(wizard.Dependencies.CanContinue);
    }

    [Fact]
    public async Task A_run_that_selected_nothing_names_the_required_ones_still_missing()
    {
        using var root = new TemporaryDirectory("LocalAi-prerequisite-still-needed");
        var wizard = Wizard(root, () => HermeticDiagnosis.Supported(
            git: Missing("git"),
            winGet: Missing("winget"),
            ollama: Missing("ollama")));
        await wizard.CheckPrerequisitesAgainAsync();
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            if (wizard.CurrentPage == InstallerPage.Models)
            {
                wizard.Models.Mode = ModelSelectionMode.Skip;
            }

            Assert.True(wizard.MoveNext());
        }

        wizard.SetReviewConfirmed(true);
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        var row = Assert.Single(wizard.Finish.Results, row => row.Title == PageLabels.ResultPrerequisites);
        Assert.Equal(RunMark.Warned, row.Mark);
        Assert.Equal("nothing was selected; still needed, install by hand: Git, Ollama", row.Detail);
    }

    [Fact]
    public void The_hint_sits_under_its_row_and_the_recipe_note_keeps_to_rows_without_one()
    {
        var template = Window().Descendants(Presentation + "ItemsControl")
            .Single(items => items.Attribute("ItemsSource")?.Value == "{Binding Dependencies.Dependencies}");
        var texts = template.Descendants(Presentation + "TextBlock").ToList();

        var hint = Assert.Single(texts, text => text.Attribute("Text")?.Value == "{Binding InstallerHint, Mode=OneWay}");
        Assert.Contains(
            hint.Descendants(Presentation + "DataTrigger"),
            trigger => trigger.Attribute("Binding")?.Value == "{Binding HasInstallerHint}" &&
                trigger.Attribute("Value")?.Value == "True");
        var recipe = Assert.Single(texts, text => text.Attribute("Text")?.Value == "{vm:Label NoAutomatedInstaller}");
        Assert.Contains(
            recipe.Descendants(Presentation + "DataTrigger"),
            trigger => trigger.Attribute("Binding")?.Value == "{Binding HasAutomatedInstaller}" &&
                trigger.Attribute("Value")?.Value == "False");
    }

    [Fact]
    public void Check_again_is_on_the_prerequisites_page()
    {
        var page = Window().Descendants(Presentation + "StackPanel")
            .Single(panel => panel.Descendants(Presentation + "ItemsControl")
                .Any(items => items.Attribute("ItemsSource")?.Value == "{Binding Dependencies.Dependencies}"));
        var button = Assert.Single(
            page.Descendants(Presentation + "Button"),
            button => button.Attribute("Command")?.Value == "{Binding CheckPrerequisitesAgainCommand}");

        Assert.Equal("{vm:Label CheckAgain}", button.Attribute("Content")?.Value);
        Assert.Equal(
            "{Binding Dependencies.NeedsCheckAgain, Converter={StaticResource BoolToVisibility}}",
            button.Attribute("Visibility")?.Value);
    }

    private static XDocument Window() => XDocument.Parse(InstallerMarkup.Read("MainWindow.xaml"));

    private static DependencySelection Row(DependenciesPageViewModel page, string id) =>
        page.Dependencies.Single(row => row.Id == id);

    private static DependencySnapshot Missing(string name) =>
        new(name, DependencyState.NotFound, null, null, null);

    /// <summary>On the prerequisites page, with the check answered by <paramref name="detect"/>.</summary>
    private static InstallerWizardViewModel Wizard(TemporaryDirectory root, Func<EnvironmentDiagnosis> detect)
    {
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            detectEnvironment: _ => Task.FromResult(detect()))
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
        return wizard;
    }
}
