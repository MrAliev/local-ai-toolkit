using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The journal is English and stays so; the report built from it is read in the reader's
/// language. A completed step is worded by its kind rather than by the journal's detail, and the
/// stop is found by its kind rather than by its description (#614).
/// </summary>
public sealed class TheRemovalReportSpeaksTheReadersLanguageTests : IDisposable
{
    private static readonly string[] JournalDetails =
    [
        "Removed.",
        "Disconnected.",
        "dispatcher(s) removed.",
        "Removed from the user PATH.",
        "Stopped.",
        "Ask Local AI to finish and exit",
    ];

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task Each_completed_step_is_worded_by_its_kind()
    {
        var (plan, lines) = await RemoveEverything();

        Assert.Equal("Stopped Local AI's running tools and broker", lines[0]);
        Assert.All(Configurations(plan), files =>
            Assert.Contains("Updated " + files + " — Local AI disconnected", lines));
        Assert.All(HookDirectories(plan), directory =>
            Assert.Contains("Removed Local AI hooks from " + directory, lines));
        Assert.All(plan.Paths, path => Assert.Contains("Removed " + path.Path, lines));
        Assert.Contains("Removed from PATH: " + machine.Layout.LauncherDirectory, lines);
        Assert.Contains(
            "Apps & features entry no longer listed; the uninstaller copy in " + UninstallerDirectory + " is gone",
            lines);
        AssertNoJournalDetail(lines);
    }

    [Fact]
    public async Task Each_completed_step_is_worded_by_its_kind_in_russian()
    {
        var (plan, lines) = await RemoveEverything(InstallerLanguage.Russian);

        Assert.Equal("Остановлены запущенные инструменты и брокер Local AI", lines[0]);
        Assert.All(Configurations(plan), files =>
            Assert.Contains("Обновлено " + files + " — Local AI отключён", lines));
        Assert.All(HookDirectories(plan), directory =>
            Assert.Contains("Удалены хуки Local AI из " + directory, lines));
        Assert.All(plan.Paths, path => Assert.Contains("Удалено " + path.Path, lines));
        Assert.Contains("Удалено из PATH: " + machine.Layout.LauncherDirectory, lines);
        Assert.Contains(
            "Запись в «Приложениях и возможностях» больше не значится; копии деинсталлятора в " +
            UninstallerDirectory + " больше нет",
            lines);
        AssertNoJournalDetail(lines);
    }

    /// <summary>A refused run: the stop is the one step that ran, and it comes before what never started.</summary>
    [Fact]
    public async Task A_failed_stop_is_named_by_its_kind_and_keeps_its_own_detail()
    {
        var plan = await Plan();
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        var step = journal.BeginStep(InstallerRunEffectKind.ProcessStop, "Ask the broker to go");
        journal.FailStep(step, "The stop request timed out.");

        Assert.Equal(
            "Failed: stopping Local AI's running tools and broker — The stop request timed out.",
            Lines(plan, journal)[0]);
        Assert.Equal(
            new RunResultRow(RunMark.Failed, "Local AI's running tools and broker", "Failed — The stop request timed out."),
            Rows(plan, journal)[0]);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Не выполнено: остановка запущенных инструментов и брокера Local AI — The stop request timed out.",
            Lines(plan, journal)[0]);
        Assert.Equal(
            new RunResultRow(RunMark.Failed, "Запущенные инструменты и брокер Local AI", "Не выполнено — The stop request timed out."),
            Rows(plan, journal)[0]);
    }

    [Fact]
    public async Task A_stop_that_never_finished_has_an_unknown_outcome()
    {
        var plan = await Plan();
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        journal.BeginStep(InstallerRunEffectKind.ProcessStop, "Ask the broker to go");

        Assert.Equal("Outcome unknown: stopping Local AI's running tools and broker", Lines(plan, journal)[0]);
        Assert.Equal(
            new RunResultRow(RunMark.Warned, "Local AI's running tools and broker", "Outcome unknown"),
            Rows(plan, journal)[0]);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Результат неизвестен: остановка запущенных инструментов и брокера Local AI",
            Lines(plan, journal)[0]);
        Assert.Equal(
            new RunResultRow(RunMark.Warned, "Запущенные инструменты и брокер Local AI", "Результат неизвестен"),
            Rows(plan, journal)[0]);
    }

    /// <summary>
    /// On the finish page the title names what a step was about and the line under it says what
    /// happened, so neither the stop nor the hooks leave a line empty or a preposition hanging.
    /// </summary>
    [Theory]
    [InlineData(InstallerLanguage.English, "Local AI's running tools and broker", "Stopped", "Local AI hooks removed")]
    [InlineData(InstallerLanguage.Russian, "Запущенные инструменты и брокер Local AI", "Остановлены", "Хуки Local AI удалены")]
    public async Task The_finish_page_titles_the_stop_and_the_hooks_and_says_below_what_happened(
        InstallerLanguage language, string stop, string stopped, string hooksRemoved)
    {
        var (plan, entries) = await RemoveEverythingReported(language);
        var rows = entries.Select(RemovalReportPresentation.Row).ToArray();

        Assert.Equal(new RunResultRow(RunMark.Done, stop, stopped), rows[0]);
        Assert.NotEmpty(HookDirectories(plan));
        Assert.All(HookDirectories(plan), directory =>
            Assert.Contains(new RunResultRow(RunMark.Done, directory, hooksRemoved), rows));
    }

    /// <summary>
    /// A journal step the report has no wording for keeps the journal's English description,
    /// marked as the journal's own words, in every state and on both surfaces.
    /// </summary>
    [Theory]
    [InlineData(InstallerLanguage.English, InstallerRunStepStatus.Completed,
        "Completed: Pull a model (journal step)", RunMark.Done, "Pull a model (journal step)", "Completed")]
    [InlineData(InstallerLanguage.English, InstallerRunStepStatus.Failed,
        "Failed: Pull a model (journal step) — The pull was refused.", RunMark.Failed,
        "Pull a model (journal step)", "Failed — The pull was refused.")]
    [InlineData(InstallerLanguage.English, InstallerRunStepStatus.Running,
        "Outcome unknown: Pull a model (journal step)", RunMark.Warned, "Pull a model (journal step)", "Outcome unknown")]
    [InlineData(InstallerLanguage.Russian, InstallerRunStepStatus.Completed,
        "Выполнено: Pull a model (шаг журнала, на английском)", RunMark.Done,
        "Pull a model (шаг журнала, на английском)", "Выполнено")]
    [InlineData(InstallerLanguage.Russian, InstallerRunStepStatus.Failed,
        "Не выполнено: Pull a model (шаг журнала, на английском) — The pull was refused.", RunMark.Failed,
        "Pull a model (шаг журнала, на английском)", "Не выполнено — The pull was refused.")]
    [InlineData(InstallerLanguage.Russian, InstallerRunStepStatus.Running,
        "Результат неизвестен: Pull a model (шаг журнала, на английском)", RunMark.Warned,
        "Pull a model (шаг журнала, на английском)", "Результат неизвестен")]
    public async Task A_step_of_unknown_kind_is_marked_as_the_journals_own_words(
        InstallerLanguage language, InstallerRunStepStatus status, string line, RunMark mark, string title, string detail)
    {
        var plan = await Plan();
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        var step = journal.BeginStep(InstallerRunEffectKind.ModelInstall, "Pull a model");
        if (status == InstallerRunStepStatus.Completed)
        {
            journal.CompleteStep(step, "Pulled.", isReversible: false);
        }
        else if (status == InstallerRunStepStatus.Failed)
        {
            journal.FailStep(step, "The pull was refused.");
        }

        InstallerCulture.Current = language;

        var lines = Lines(plan, journal);
        Assert.Equal(line, Assert.Single(lines, item => item.Contains("Pull a model", StringComparison.Ordinal)));
        Assert.DoesNotContain("Pulled.", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.Equal(
            new RunResultRow(mark, title, detail),
            Assert.Single(Rows(plan, journal), row => row.Title.Contains("Pull a model", StringComparison.Ordinal)));
    }

    /// <summary>What the plan keeps is named by the plan rather than by the journal, and is not marked.</summary>
    [Fact]
    public async Task Only_a_journal_step_carries_the_journal_mark()
    {
        var plan = await Plan();
        Assert.NotEmpty(plan.Retained);
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        journal.BeginStep(InstallerRunEffectKind.ModelInstall, "Pull a model");

        Assert.Single(Lines(plan, journal), item => item.Contains(" (journal step)", StringComparison.Ordinal));
        Assert.Single(Rows(plan, journal), row => row.Title.Contains(" (journal step)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_wizard_reports_a_finished_removal_in_russian()
    {
        var wizard = new UninstallWizardViewModel(RemovalPreset.ReinstallFriendly, true,
            machine.Layout, machine.Home, new Exits(), machine.HooksPathReader)
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        wizard.Presentation.ChooseLanguage(InstallerLanguage.Russian);

        var lines = wizard.Report.Split(Environment.NewLine);
        Assert.Equal("Остановлены запущенные инструменты и брокер Local AI", lines[0]);
        AssertNoJournalDetail(lines);
        Assert.Equal(
            new RunResultRow(RunMark.Done, "Запущенные инструменты и брокер Local AI", "Остановлены"),
            wizard.Results[0]);
    }

    private string UninstallerDirectory => new UninstallRegistration(machine.Layout).UninstallerDirectory;

    private Task<UninstallPlan> Plan() =>
        machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.FullUninstall), TestContext.Current.CancellationToken);

    private async Task<(UninstallPlan Plan, string[] Lines)> RemoveEverything(
        InstallerLanguage language = InstallerLanguage.English)
    {
        var (plan, entries) = await RemoveEverythingReported(language);
        return (plan, entries.Select(RemovalReportPresentation.Line).ToArray());
    }

    /// <summary>The real runner, so the journal holds exactly what a removal writes.</summary>
    private async Task<(UninstallPlan Plan, IReadOnlyList<RemovalReportEntry> Entries)> RemoveEverythingReported(
        InstallerLanguage language)
    {
        var plan = await Plan() with { RemovesAppsAndFeaturesEntry = true, RemovesLauncherFromPath = true };
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        var outcome = await new UninstallRunner(
                machine.Layout,
                new Exits(),
                registrySubKey: RemovalFixture.UnusedRegistrySubKey,
                userPath: new UserPath(machine.Layout.LauncherDirectory))
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);
        InstallerCulture.Current = language;
        return (plan, UninstallReport.Capture(plan, journal.Snapshot, machine.Layout));
    }

    private string[] Lines(UninstallPlan plan, InstallerRunJournal journal) =>
        UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
            .Select(RemovalReportPresentation.Line)
            .ToArray();

    private RunResultRow[] Rows(UninstallPlan plan, InstallerRunJournal journal) =>
        UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
            .Select(RemovalReportPresentation.Row)
            .ToArray();

    private static IEnumerable<string> Configurations(UninstallPlan plan) =>
        plan.AgentConfigurations
            .Where(configuration => configuration.HasChanges)
            .Select(configuration => string.Join(", ", configuration.Files.Select(file => file.Path)));

    private static IEnumerable<string> HookDirectories(UninstallPlan plan) =>
        plan.Hooks.Where(hook => hook.HasWork).Select(hook => hook.CommonDirectory);

    private static void AssertNoJournalDetail(IEnumerable<string> lines)
    {
        var report = string.Join("\n", lines);
        Assert.All(JournalDetails, detail => Assert.DoesNotContain(detail, report, StringComparison.Ordinal));
    }

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }

    private sealed class UserPath(string value) : IUserPathStore
    {
        private string? value = value;

        public string? Read() => value;

        public void Write(string written) => value = written;
    }
}
