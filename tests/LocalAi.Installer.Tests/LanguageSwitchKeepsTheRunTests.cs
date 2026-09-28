using LocalAi.Contracts;
using LocalAi.Contracts.Activation;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The language can be changed on any screen now, not only on the first (#602). What changes is
/// the words. The page, the answers already given, the consent and the plan it covers stay
/// exactly as they were, and nothing that already ran runs again.
/// </summary>
public sealed class LanguageSwitchKeepsTheRunTests : IDisposable
{
    private readonly TemporaryDirectory root = new("LocalAi-language-switch");
    private readonly RemovalFixture machine = new();

    public void Dispose()
    {
        machine.Dispose();
        root.Dispose();
    }

    [Fact]
    public void The_review_page_keeps_its_answers_and_its_consent()
    {
        var presentation = Presentation();
        var detections = 0;
        var feed = new CountingFeed();
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            feed,
            detectEnvironment: _ =>
            {
                detections++;
                return Task.FromException<EnvironmentDiagnosis>(
                    new InvalidOperationException("not expected to run"));
            },
            presentation: presentation)
        {
            LogDirectory = root.Combine("logs"),
        };
        wizard.Package.SourceFolder = root.Combine("release");
        wizard.Residency.Policy = ModelResidencyPolicy.AllowCpu;
        wizard.Models.IsChooseExact = true;
        var model = wizard.Models.CatalogModels[^1];
        wizard.Models.SelectedModel = model;
        var context = wizard.Models.AvailableContexts[^1];
        wizard.Models.SelectedContext = context;
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        WalkToTheReview(wizard);
        wizard.SetReviewConfirmed(true);
        var englishTitle = wizard.StepTitle;
        var review = wizard.ReviewText;
        Assert.True(wizard.CanRun);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(InstallerPage.Confirm, wizard.CurrentPage);
        Assert.True(wizard.Review.IsConfirmed);
        Assert.True(wizard.CanRun);
        Assert.Equal(root.Combine("release"), wizard.Package.SourceFolder);
        Assert.Equal(ModelResidencyPolicy.AllowCpu, wizard.Residency.Policy);
        Assert.True(wizard.Models.IsChooseExact);
        Assert.Same(model, wizard.Models.SelectedModel);
        Assert.Equal(context, wizard.Models.SelectedContext);
        Assert.Equal(AgentChoice.McpOnly, wizard.Agents.Agents.Single(agent => agent.Agent == "codex").Choice);
        Assert.Equal(0, detections);
        Assert.Equal(0, feed.Calls);
        Assert.NotEqual(englishTitle, wizard.StepTitle);
        Assert.NotEqual(review, wizard.ReviewText);
    }

    [Fact]
    public void The_wizard_repaints_its_frame_in_the_language_chosen()
    {
        var presentation = Presentation();
        var wizard = new InstallerWizardViewModel(presentation: presentation);
        var announced = new List<string?>();
        wizard.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Установка Local AI", wizard.WindowTitle);
        Assert.Equal("Проверка системы", wizard.StepList[0].Title);
        Assert.Equal("Шаг 1 из " + wizard.StepList.Count, wizard.StepStatus);
        foreach (var property in new[]
                 {
                     nameof(InstallerWizardViewModel.WindowTitle),
                     nameof(InstallerWizardViewModel.StepTitle),
                     nameof(InstallerWizardViewModel.StepDescription),
                     nameof(InstallerWizardViewModel.StepStatus),
                     nameof(InstallerWizardViewModel.ConsentText),
                     nameof(InstallerWizardViewModel.ActionText),
                     nameof(InstallerWizardViewModel.CancelButtonText),
                     nameof(InstallerWizardViewModel.ReviewText),
                     nameof(InstallerWizardViewModel.VersionContext),
                 })
        {
            Assert.Contains(announced, name => InstallerPresentationTests.Announces(name, property));
        }
    }

    /// <summary>
    /// A page's own sentences are computed from what it holds, so it only has to be told to read
    /// them again. The residency warning stands in for the rest of them.
    /// </summary>
    [Fact]
    public void The_pages_behind_the_wizard_are_told_as_well()
    {
        var presentation = Presentation();
        var wizard = new InstallerWizardViewModel(presentation: presentation);
        wizard.Residency.Policy = ModelResidencyPolicy.AllowCpu;
        var english = wizard.Residency.Warning;
        var announced = new List<string?>();
        wizard.Residency.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Contains(
            announced,
            name => InstallerPresentationTests.Announces(name, nameof(ResidencyPageViewModel.Warning)));
        Assert.NotEqual(english, wizard.Residency.Warning);
        Assert.Equal(ModelResidencyPolicy.AllowCpu, wizard.Residency.Policy);
    }

    [Fact]
    public void Every_page_behind_the_wizard_is_told()
    {
        var presentation = Presentation();
        var wizard = new InstallerWizardViewModel(presentation: presentation);
        var pages = new ObservableObject[]
        {
            wizard.Diagnose, wizard.Dependencies, wizard.Package, wizard.Models,
            wizard.Residency, wizard.Agents, wizard.Review, wizard.Finish,
        };
        var told = new HashSet<ObservableObject>();
        foreach (var page in pages)
        {
            page.PropertyChanged += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName))
                {
                    told.Add(page);
                }
            };
        }

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Empty(pages.Where(page => !told.Contains(page)).Select(page => page.GetType().Name));
    }

    /// <summary>
    /// A closed window's view model is let go. It would otherwise stay reachable from the shared
    /// settings for the life of the process, and every later switch would repaint a window that
    /// is gone.
    /// </summary>
    [Fact]
    public void A_wizard_that_has_been_let_go_no_longer_follows()
    {
        var presentation = Presentation();
        var wizard = new InstallerWizardViewModel(presentation: presentation);
        wizard.DetachPresentation();
        var announced = 0;
        wizard.PropertyChanged += (_, _) => announced++;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task The_removal_review_keeps_its_plan_and_its_consent()
    {
        var presentation = Presentation();
        var wizard = await Removal(presentation);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        // Gone from the disk once the plan names it: a plan built again would not name it.
        var jobs = Path.Combine(machine.Runtime, "jobs");
        Assert.Contains(jobs, wizard.PreviewText, StringComparison.Ordinal);
        Directory.Delete(jobs, recursive: true);
        Assert.True(wizard.CanRun);
        var announced = new List<string?>();
        wizard.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(UninstallPage.Confirm, wizard.CurrentPage);
        Assert.True(wizard.IsConfirmed);
        Assert.True(wizard.CanRun);
        Assert.Contains(jobs, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Equal("Готово к удалению", wizard.StepTitle);
        Assert.Equal("Что удалить", wizard.StepList[0].Title);
        Assert.Contains(
            announced,
            name => InstallerPresentationTests.Announces(name, nameof(UninstallWizardViewModel.StepTitle)));
    }

    /// <summary>The removal page's rows and presets are labels of the matrix: they switch, and every tick stays.</summary>
    [Fact]
    public async Task The_removal_choices_switch_language_and_keep_every_tick()
    {
        var presentation = Presentation();
        var wizard = await Removal(presentation);
        wizard.SelectedPreset = RemovalPreset.ReinstallFriendly;
        var binaries = wizard.Rows.Single(row => row.Item == RemovalItem.Binaries);
        wizard.Rows.Single(row => row.Item == RemovalItem.RepositoryIndexes).IsSelected = true;
        var ticks = wizard.Rows.Select(row => row.IsSelected).ToArray();
        var rowAnnounced = new List<string?>();
        binaries.PropertyChanged += (_, e) => rowAnnounced.Add(e.PropertyName);
        var presetAnnounced = new List<string?>();
        wizard.Presets[0].PropertyChanged += (_, e) => presetAnnounced.Add(e.PropertyName);
        var repositoryAnnounced = new List<string?>();
        Assert.NotEmpty(wizard.Repositories);
        wizard.Repositories[0].PropertyChanged += (_, e) => repositoryAnnounced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Бинарные файлы, launcher, указатель версии", binaries.Title);
        Assert.Contains(rowAnnounced, name => InstallerPresentationTests.Announces(name, nameof(RemovalRow.Title)));
        Assert.Contains(rowAnnounced, name => InstallerPresentationTests.Announces(name, nameof(RemovalRow.Note)));
        Assert.Contains(
            presetAnnounced,
            name => InstallerPresentationTests.Announces(name, nameof(UninstallPresetOption.Title)));
        Assert.Contains(
            repositoryAnnounced,
            name => InstallerPresentationTests.Announces(name, nameof(RepositoryRow.StateText)));
        Assert.Equal(ticks, wizard.Rows.Select(row => row.IsSelected));
        Assert.Equal(RemovalPreset.ReinstallFriendly, wizard.SelectedPreset);
    }

    [Fact]
    public async Task A_removal_wizard_that_has_been_let_go_no_longer_follows()
    {
        var presentation = Presentation();
        var wizard = await Removal(presentation);
        wizard.DetachPresentation();
        var announced = 0;
        wizard.PropertyChanged += (_, _) => announced++;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(0, announced);
    }

    /// <summary>
    /// The start screen is hidden, not closed, while a wizard runs — so a language chosen in the
    /// wizard has to be the one it speaks when Back brings it back, with the errand still chosen.
    /// </summary>
    [Fact]
    public void The_start_screen_follows_a_choice_made_elsewhere_and_keeps_the_errand()
    {
        var presentation = Presentation();
        var start = new InstallerStartViewModel(
            machine.LocalAppData,
            readInstalledVersion: () => new InstalledVersion("467ed5f0f9bf", "0.1.51"),
            presentation: presentation);
        start.Select(StartChoice.UpdateOrRepair);
        var announced = new List<string?>();
        start.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Local AI 0.1.51 установлен на этом компьютере.", start.Headline);
        Assert.Equal(StartChoice.UpdateOrRepair, start.Selected);
        Assert.True(start.Option(StartChoice.UpdateOrRepair).IsSelected);
        Assert.Equal("Обновить или восстановить", start.Option(StartChoice.UpdateOrRepair).Title);
        Assert.Contains(
            announced,
            name => InstallerPresentationTests.Announces(name, nameof(InstallerStartViewModel.Headline)));
    }

    /// <summary>
    /// The start window closes for good when a clean reinstall hands over to the installation
    /// wizard, so its view model is let go like a wizard's.
    /// </summary>
    [Fact]
    public void A_start_screen_that_has_been_let_go_no_longer_follows()
    {
        var presentation = Presentation();
        var start = new InstallerStartViewModel(
            machine.LocalAppData,
            readInstalledVersion: () => new InstalledVersion("467ed5f0f9bf", "0.1.51"),
            presentation: presentation);
        var row = start.Actions[0];
        start.DetachPresentation();

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Same(row, start.Actions[0]);
    }

    /// <summary>Every window binds its panel to the one object the others change.</summary>
    [Fact]
    public async Task Every_view_model_hands_out_the_settings_it_was_given()
    {
        var presentation = Presentation();

        Assert.Same(presentation, new InstallerWizardViewModel(presentation: presentation).Presentation);
        Assert.Same(presentation, (await Removal(presentation)).Presentation);
        Assert.Same(
            presentation,
            new InstallerStartViewModel(machine.LocalAppData, presentation: presentation).Presentation);
    }

    private InstallerPresentation Presentation() =>
        new(new InstallerPreferencesStore(root.Combine("preferences")));

    private async Task<UninstallWizardViewModel> Removal(InstallerPresentation presentation)
    {
        var wizard = new UninstallWizardViewModel(
            RemovalPreset.FullUninstall,
            offersInstallAfterwards: false,
            machine.Layout,
            machine.Home,
            new StubProcessRunner(),
            machine.HooksPathReader,
            presentation: presentation)
        {
            LogDirectory = root.Combine("removal-journal"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    private static void WalkToTheReview(InstallerWizardViewModel wizard)
    {
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        foreach (var name in new[]
                 {
                     "Git", "Ollama", "GitHubCli", "DotNetSdk",
                     "NodeJs", "ScipTypeScript", "Python", "ScipPython",
                 })
        {
            wizard.Dependencies.SetInstalled(name, true);
        }

        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false, false));
    }

    /// <summary>A feed that only counts: a language switch has no business asking it anything.</summary>
    private sealed class CountingFeed : IReleaseFeed
    {
        public int Calls { get; private set; }

        public Task<string> ResolveTagAsync(string requestedTag, CancellationToken ct)
        {
            Calls++;
            return Task.FromException<string>(new ReleaseResolutionException("not expected"));
        }

        public Task<ResolvedRelease> ResolveAsync(
            string tag,
            string workingDirectory,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromException<ResolvedRelease>(new ReleaseResolutionException("not expected"));
        }

        public Task<string> DownloadPackageAsync(
            string tag,
            string workingDirectory,
            IProgress<long>? bytesDownloaded,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromException<string>(new ReleaseResolutionException("not expected"));
        }
    }
}
