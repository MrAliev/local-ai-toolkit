using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;
using LocalAi.Tests.Shared;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Text a page built from a result — a check's outcome, a verified release, a model's verdict, a
/// finish row — used to keep the language it was built in when the language changed (#602). The
/// outcome stays; only its words follow the language now.
/// </summary>
public sealed class ResultsFollowTheLanguageTests : IDisposable
{
    private readonly TemporaryDirectory root = new("LocalAi-results-language");
    private readonly RemovalFixture machine = new();

    public void Dispose()
    {
        machine.Dispose();
        root.Dispose();
    }

    [Fact]
    public void The_system_check_keeps_its_findings_and_changes_their_names()
    {
        var (wizard, presentation) = Wizard();
        wizard.Diagnose.Load(Diagnosis());
        var checks = wizard.Diagnose.Checks.Count;
        var detail = wizard.Diagnose.Checks[0].Detail;
        var rows = wizard.Diagnose.Checks.ToArray();

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        // Reworded in place. Replaced rows equal to the ones they replace — a tool's name,
        // version and path do not translate — dropped out of the automation tree on the built
        // installer, though the table on screen was whole.
        Assert.Equal(rows, wizard.Diagnose.Checks, ReferenceEqualityComparer.Instance);
        Assert.Equal(checks, wizard.Diagnose.Checks.Count);
        Assert.Equal("Операционная система", wizard.Diagnose.Checks[0].Name);
        Assert.Equal(detail, wizard.Diagnose.Checks[0].Detail);
        Assert.Equal("Вход в GitHub", wizard.Diagnose.Checks[4].Name);
        Assert.Equal("ОК", wizard.Diagnose.Checks[0].StatusText);
    }

    [Fact]
    public void The_adapter_the_rule_is_about_is_named_in_the_new_language()
    {
        var (wizard, presentation) = Wizard();
        wizard.Residency.WeighedAdapter = new GpuAdapterSnapshot("gpu-1", "GPU 1", 8_000, false);
        Assert.StartsWith("Found: ", wizard.Residency.AdapterFound, StringComparison.Ordinal);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.StartsWith("Найден: ", wizard.Residency.AdapterFound, StringComparison.Ordinal);
        Assert.Contains("GPU 1", wizard.Residency.AdapterFound, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interrupted_run_is_described_in_the_new_language_from_the_same_journal()
    {
        var logs = root.Combine("interrupted");
        var journal = InstallerRunJournal.Start(logs);
        var applied = journal.BeginStep(InstallerRunEffectKind.ResidencyPolicy, "Model residency policy");
        journal.CompleteStep(applied, "written", isReversible: true);
        journal.Dispose();
        var (wizard, presentation) = Wizard();
        wizard.LogDirectory = logs;
        wizard.LoadInterruptedRunJournal();
        Assert.Contains("applied", wizard.InterruptedRunNotice, StringComparison.Ordinal);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.True(wizard.HasInterruptedRun);
        Assert.StartsWith("Предыдущий запуск установщика", wizard.InterruptedRunNotice, StringComparison.Ordinal);
        Assert.Contains("Model residency policy: применено.", wizard.InterruptedRunNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_removal_wizard_says_why_it_is_blocked_in_the_new_language()
    {
        Directory.Delete(machine.Runtime, recursive: true);
        var (wizard, presentation) = await Removal();
        Assert.True(wizard.IsBlocked);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.True(wizard.IsBlocked);
        Assert.Contains("нет установки LocalAi", wizard.BlockingNotice!, StringComparison.Ordinal);
        Assert.Equal("Готово", wizard.ProgressText);
    }

    [Fact]
    public async Task The_removal_review_rewords_the_same_plan()
    {
        var (wizard, presentation) = await Removal(RemovalPreset.ReinstallFriendly, offersInstallAfterwards: true);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        // Gone from the disk once the plan names it: a plan built again would not name it.
        var jobs = Path.Combine(machine.Runtime, "jobs");
        Assert.Contains(jobs, wizard.PreviewText, StringComparison.Ordinal);
        Directory.Delete(jobs, recursive: true);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.True(wizard.CanRun);
        Assert.Contains("  удалить " + jobs, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Contains("перепишет все три", wizard.KeepNotice, StringComparison.Ordinal);
    }

    /// <summary>The removal half of a reinstall: it takes the binaries and never plans the registry entry.</summary>
    [Fact]
    public async Task The_removal_outcome_is_reported_in_the_new_language()
    {
        var (wizard, presentation) = await Removal(RemovalPreset.ReinstallFriendly, offersInstallAfterwards: true);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        var removed = wizard.Report.Split('\n').Count(line =>
            line.StartsWith("Removed ", StringComparison.Ordinal) &&
            !line.StartsWith("Removed hook ", StringComparison.Ordinal));
        Assert.True(removed > 0);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.StartsWith("Удалено путей из ", wizard.Summary, StringComparison.Ordinal);
        Assert.Equal(removed, wizard.Report.Split('\n').Count(line => line.StartsWith("Удалено ", StringComparison.Ordinal)));
        Assert.Contains("Оставлено: ", wizard.Report, StringComparison.Ordinal);
        Assert.Equal("Завершено", wizard.ProgressText);
    }

    [Fact]
    public async Task A_refused_removal_says_so_in_the_new_language()
    {
        var (wizard, presentation) = await Removal(
            RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true,
            new StubProcessRunner { Result = new(1, string.Empty, "broker_still_running", false, false) });
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Nothing was removed.", wizard.Report);
        var refusal = wizard.Summary;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Ничего не удалено.", wizard.Report);
        Assert.Equal("Отказано", wizard.ProgressText);
        Assert.Equal(refusal, wizard.Summary);
    }

    [Fact]
    public async Task A_removal_that_left_something_behind_counts_it_in_the_new_language()
    {
        var held = Path.Combine(machine.Runtime, "jobs", "job.json");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        var (wizard, presentation) = await Removal(RemovalPreset.ReinstallFriendly, offersInstallAfterwards: true);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("Could not be removed: 1;", wizard.Summary, StringComparison.Ordinal);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.StartsWith("Не удалось удалить: 1;", wizard.Summary, StringComparison.Ordinal);
        Assert.Contains("Не удалось удалить " + Path.Combine(machine.Runtime, "jobs"), wizard.Report, StringComparison.Ordinal);
        Assert.Equal("Завершено с проблемами", wizard.ProgressText);
    }

    /// <summary>
    /// The review is one string the wizard builds out of its pages, and a binding reads it the
    /// moment it is announced — so it is announced after every page has its new words.
    /// </summary>
    [Fact]
    public void The_review_is_announced_once_every_page_has_its_new_words()
    {
        var (wizard, presentation) = Wizard();
        wizard.Models.ApplyRecommendation(CatalogRecommendation.Empty, residencyRequiresVideoMemory: true);
        string? read = null;
        wizard.PropertyChanged += (_, e) =>
        {
            if (InstallerPresentationTests.Announces(e.PropertyName, nameof(InstallerWizardViewModel.ReviewText)))
            {
                read = wizard.ReviewText;
            }
        };

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.NotNull(read);
        Assert.Contains("размер неизвестен", read, StringComparison.Ordinal);
        Assert.DoesNotContain("size unknown", read, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "результат проверки неизвестен")]
    [InlineData(true, "проверка отменена")]
    public void A_model_row_the_run_never_answered_for_keeps_its_state_and_changes_its_words(
        bool cancelled,
        string unanswered)
    {
        var (wizard, presentation) = Wizard();
        foreach (var model in new[] { "working", "waiting", "rejected", "failed" })
        {
            wizard.ApplyModelStep(new ModelProvisioningProgress(model, 2048, ModelProvisioningPhase.Planned, 0, 4));
        }

        wizard.ApplyModelStep(new ModelProvisioningProgress("working", 2048, ModelProvisioningPhase.Checking, 0, 4));
        wizard.ModelRuns[2].Settled(
            BrokerModelInstallOutcome.RejectedResidency,
            downloaded: false,
            [new ModelFallbackSuggestion("rejected", 1024, 1)],
            "residency");
        wizard.ModelRuns[3].Settled(BrokerModelInstallOutcome.Failed, downloaded: false, [], "pull_failed");
        wizard.SettleModelRuns(cancelled);
        var states = wizard.ModelRuns.Select(row => row.State).ToArray();

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal(states, wizard.ModelRuns.Select(row => row.State));
        Assert.Equal(unanswered, wizard.ModelRuns[0].Detail);
        Assert.Equal("не дошло — настройка моделей остановилась раньше", wizard.ModelRuns[1].Detail);
        Assert.Equal(
            "не помещается, поэтому не используется. Помещаются варианты меньше: rejected / 1024",
            wizard.ModelRuns[2].Detail);
        Assert.Equal("не удалось (pull_failed)", wizard.ModelRuns[3].Detail);
    }

    /// <summary>A real run is the only way to these two rows, so their words are asked for directly.</summary>
    [Fact]
    public void The_package_and_path_rows_are_worded_in_the_language_chosen()
    {
        var installed = new PackageOutcome(
            PackageOutcomeKind.Result,
            LocalAi.Installer.Core.Activation.LocalAiPackageInstallStatus.Installed,
            "0.2.1");
        var conflict = new PackageOutcome(
            PackageOutcomeKind.Result,
            LocalAi.Installer.Core.Activation.LocalAiPackageInstallStatus.ImmutableConflict,
            Reason: "files differ");
        Assert.Equal("installed, version 0.2.1", RunResults.PackageDetail(installed, installed: true));
        Assert.Equal("added for this account", RunResults.PathDetail(PathOutcome.Added));

        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Equal("установлен, версия 0.2.1", RunResults.PackageDetail(installed, installed: true));
        Assert.Equal(
            "не установлен: в каталоге этой версии уже лежат другие файлы — files differ",
            RunResults.PackageDetail(conflict, installed: false));
        Assert.Equal(
            "не установлен, проверенный релиз не выбран",
            RunResults.PackageDetail(new PackageOutcome(PackageOutcomeKind.NoRelease), installed: false));
        Assert.Equal(
            "не установлен — layout is unsafe",
            RunResults.PackageDetail(new PackageOutcome(PackageOutcomeKind.Refused, Reason: "layout is unsafe"), installed: false));
        Assert.Equal("добавлен для этой учётной записи", RunResults.PathDetail(PathOutcome.Added));
        Assert.Equal("уже был прописан", RunResults.PathDetail(PathOutcome.AlreadyThere));
        Assert.Equal("не удалось изменить", RunResults.PathDetail(PathOutcome.Failed));
    }

    [Fact]
    public void A_prerequisite_row_keeps_its_consent_and_changes_its_words()
    {
        var (wizard, presentation) = Wizard();
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetConsent("DotNetSdk", true);
        var git = wizard.Dependencies.Dependencies.Single(row => row.Id == "Git");
        var sdk = wizard.Dependencies.Dependencies.Single(row => row.Id == "DotNetSdk");
        var announced = new List<string?>();
        git.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Same(git, wizard.Dependencies.Dependencies.Single(row => row.Id == "Git"));
        Assert.Equal("Git — Уже установлен", git.HeadLine);
        Assert.Equal("обязателен", git.RequirementText);
        Assert.StartsWith("необязателен: без него", sdk.RequirementText, StringComparison.Ordinal);
        Assert.True(sdk.IsConsented);
        Assert.Contains(announced, name => InstallerPresentationTests.Announces(name, nameof(DependencySelection.HeadLine)));
    }

    [Fact]
    public void A_verified_release_is_reported_in_the_new_language()
    {
        var (wizard, presentation) = Wizard();
        wizard.Package.SelectResolvedRelease(Release("0.1.51"), "0.1.51");

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Релиз 0.1.51 проверен, скачать 0 МБ.", wizard.Package.StatusText);
        Assert.Equal(PackageSourceState.Selected, wizard.Package.State);
        Assert.NotNull(wizard.Package.Resolved);
    }

    [Fact]
    public void A_release_that_could_not_be_resolved_keeps_the_reason_and_changes_the_lead()
    {
        var (wizard, presentation) = Wizard();
        wizard.Package.ReportUnavailable("the feed could not be reached");

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Релиз не определён. the feed could not be reached", wizard.Package.StatusText);
    }

    [Fact]
    public void The_automatic_model_list_is_weighed_once_and_worded_twice()
    {
        var (wizard, presentation) = Wizard();
        wizard.Models.ApplyRecommendation(CatalogRecommendation.Empty, residencyRequiresVideoMemory: true);
        Assert.StartsWith("Model sizes could not be retrieved", wizard.Models.AutomaticSummary, StringComparison.Ordinal);
        var offered = wizard.Models.AutomaticSelection.Count;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.StartsWith("Размеры моделей получить не удалось", wizard.Models.AutomaticSummary, StringComparison.Ordinal);
        Assert.Equal(offered, wizard.Models.AutomaticSelection.Count);
        Assert.All(wizard.Models.AutomaticSelection, line => Assert.EndsWith("размер неизвестен", line, StringComparison.Ordinal));
        Assert.StartsWith("Вся модель в видеопамяти", wizard.Models.RuleSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_rows_and_their_choices_change_words_and_keep_the_choice()
    {
        var (wizard, presentation) = Wizard();
        wizard.Agents.SetChoice("codex", AgentChoice.McpOnly);
        var codex = wizard.Agents.Agents.Single(agent => agent.Agent == "codex");
        var option = wizard.Agents.ChoiceOptions.Single(option => option.Choice == AgentChoice.McpOnly);
        var rowAnnounced = new List<string?>();
        codex.PropertyChanged += (_, e) => rowAnnounced.Add(e.PropertyName);
        var optionAnnounced = new List<string?>();
        option.PropertyChanged += (_, e) => optionAnnounced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Same(codex, wizard.Agents.Agents.Single(agent => agent.Agent == "codex"));
        Assert.Equal(AgentChoice.McpOnly, codex.Choice);
        Assert.Equal("Codex (не обнаружено)", codex.Heading);
        Assert.Equal("Зарегистрировать MCP-серверы", option.Title);
        Assert.Contains(rowAnnounced, name => InstallerPresentationTests.Announces(name, nameof(AgentOption.Heading)));
        Assert.Contains(optionAnnounced, name => InstallerPresentationTests.Announces(name, nameof(AgentChoiceOption.Title)));
    }

    [Fact]
    public void A_models_verdict_stands_and_its_words_follow()
    {
        var (wizard, presentation) = Wizard();
        wizard.ApplyModelStep(new ModelProvisioningProgress("a", 2048, ModelProvisioningPhase.Planned, 0, 2));
        wizard.ApplyModelStep(new ModelProvisioningProgress("b", 2048, ModelProvisioningPhase.Planned, 0, 2));
        wizard.ApplyModelStep(new ModelProvisioningProgress("a", 2048, ModelProvisioningPhase.Checking, 0, 2));
        wizard.ModelRuns[1].Settled(BrokerModelInstallOutcome.Accepted, downloaded: true, [], "ok");
        var announced = new List<string?>();
        wizard.ModelRuns[0].PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("проверяется, помещается ли в видеопамять", wizard.ModelRuns[0].Detail);
        Assert.Equal(ModelRunState.Working, wizard.ModelRuns[0].State);
        Assert.Equal("готова — скачана", wizard.ModelRuns[1].Detail);
        Assert.Equal(ModelRunState.Ready, wizard.ModelRuns[1].State);
        Assert.Contains(announced, name => InstallerPresentationTests.Announces(name, nameof(ModelRunRow.Detail)));
    }

    /// <summary>A dry run ends on the finish page with every row saying nothing was attempted.</summary>
    [Fact]
    public async Task The_finish_rows_and_the_progress_word_follow_after_a_run()
    {
        var (wizard, presentation) = Wizard();
        wizard.Diagnose.IsChecking = false;
        wizard.Diagnose.SetResult(true);
        wizard.Dependencies.SetInstalled("Git", true);
        wizard.Dependencies.SetInstalled("Ollama", true);
        while (wizard.CurrentPage != InstallerPage.Confirm)
        {
            Assert.True(wizard.MoveNext(), $"stuck on {wizard.CurrentPage}");
        }

        wizard.SetReviewConfirmed(true);
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Completed", wizard.ProgressText);
        var rows = wizard.Finish.Results.Count;

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Завершено", wizard.ProgressText);
        Assert.Equal(rows, wizard.Finish.Results.Count);
        Assert.Equal("Пакет LocalAi", wizard.Finish.Results[0].Title);
        Assert.Equal("не выполнялось", wizard.Finish.Results[0].Detail);
        Assert.Equal("Необходимые компоненты", wizard.Finish.Results[1].Title);
        Assert.Equal("ничего не выбрано", wizard.Finish.Results[1].Detail);
        Assert.Equal(InstallerPage.Finish, wizard.CurrentPage);
    }

    [Fact]
    public void An_unexpected_error_keeps_the_exception_and_changes_the_label()
    {
        var (wizard, presentation) = Wizard();
        wizard.ReportUnexpectedError(new InvalidOperationException("boom"));

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Непредвиденная ошибка: InvalidOperationException: boom", wizard.UnexpectedError);
    }

    [Fact]
    public void The_idle_progress_word_is_not_english_on_a_russian_screen()
    {
        var (wizard, presentation) = Wizard();

        presentation.ChooseLanguage(InstallerLanguage.Russian);

        Assert.Equal("Готово", wizard.ProgressText);
    }

    private (InstallerWizardViewModel Wizard, InstallerPresentation Presentation) Wizard()
    {
        var presentation = new InstallerPresentation(
            new InstallerPreferencesStore(root.Combine("preferences")));
        var wizard = new InstallerWizardViewModel(
            StartChoice.Install,
            () => null,
            presentation: presentation)
        {
            LogDirectory = root.Combine("logs"),
        };
        return (wizard, presentation);
    }

    private async Task<(UninstallWizardViewModel Wizard, InstallerPresentation Presentation)> Removal(
        RemovalPreset preset = RemovalPreset.FullUninstall,
        bool offersInstallAfterwards = false,
        IProcessRunner? processRunner = null)
    {
        var presentation = new InstallerPresentation(
            new InstallerPreferencesStore(root.Combine("preferences")));
        var wizard = new UninstallWizardViewModel(
            preset,
            offersInstallAfterwards,
            machine.Layout,
            machine.Home,
            processRunner ?? new StubProcessRunner(),
            machine.HooksPathReader,
            presentation: presentation)
        {
            LogDirectory = root.Combine("removal-journal"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return (wizard, presentation);
    }

    private static EnvironmentDiagnosis Diagnosis() =>
        new(
            new OperatingSystemSnapshot(
                "Windows 11 Pro",
                new Version(10, 0, 26100),
                System.Runtime.InteropServices.Architecture.X64,
                SupportStatus.Supported,
                SupportStatus.Supported),
            new DiskSnapshot(ObservationState.Available, 100_000_000_000, null),
            new NetworkSnapshot(ObservationState.Available, null),
            new DependencySnapshot("WinGet", DependencyState.Detected, "winget.exe", "1.10", null),
            new DependencySnapshot("Git", DependencyState.Detected, "git.exe", "2.50", null),
            new DependencySnapshot("GitHubCli", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("GitHubSignIn", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Ollama", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("DotNetSdk", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("NodeJs", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Npm", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipTypeScript", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("Python", DependencyState.NotFound, null, null, null),
            new DependencySnapshot("ScipPython", DependencyState.NotFound, null, null, null),
            new GpuSnapshot(ObservationState.Available, [new GpuAdapterSnapshot("gpu-1", "GPU 1", 8_000, false)], null),
            new ExistingLocalAiSnapshot(ExistingLocalAiState.Absent, null, null, null),
            [],
            []);

    private sealed class StubProcessRunner : IProcessRunner
    {
        public ProcessResult Result { get; init; } = new(0, string.Empty, string.Empty, false, false);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private static ResolvedRelease Release(string version) =>
        new(
            new ReleaseManifest(
                schemaVersion: 1,
                releaseVersion: version,
                versionDirectory: "0123456789ab",
                modelCatalogVersion: "1",
                protocolVersion: 1,
                buildCompatibilityId: "test",
                packageUri: new Uri($"https://example.invalid/{version}/localai-package.zip"),
                packageSize: 1024,
                packageSha256: new string('a', 64),
                requiresAuthenticode: false,
                models: []),
            [1],
            [2]);
}
