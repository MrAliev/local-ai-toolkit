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
        "Ask LocalAi to finish and exit",
    ];

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task Each_completed_step_is_worded_by_its_kind()
    {
        var (plan, lines) = await RemoveEverything();

        Assert.Equal("Stopped LocalAi's running tools and broker", lines[0]);
        Assert.All(Configurations(plan), files =>
            Assert.Contains("Updated " + files + " — LocalAi disconnected", lines));
        Assert.All(HookDirectories(plan), directory =>
            Assert.Contains("Removed LocalAi hooks from " + directory, lines));
        Assert.All(plan.Paths, path => Assert.Contains("Removed " + path.Path, lines));
        Assert.Contains("Removed from PATH: " + machine.Layout.LauncherDirectory, lines);
        Assert.Contains("Registration processed: " + UninstallerDirectory, lines);
        AssertNoJournalDetail(lines);
    }

    [Fact]
    public async Task Each_completed_step_is_worded_by_its_kind_in_russian()
    {
        var (plan, lines) = await RemoveEverything(InstallerLanguage.Russian);

        Assert.Equal("Остановлены запущенные инструменты и брокер LocalAi", lines[0]);
        Assert.All(Configurations(plan), files =>
            Assert.Contains("Обновлено " + files + " — LocalAi отключён", lines));
        Assert.All(HookDirectories(plan), directory =>
            Assert.Contains("Удалены хуки LocalAi из " + directory, lines));
        Assert.All(plan.Paths, path => Assert.Contains("Удалено " + path.Path, lines));
        Assert.Contains("Удалено из PATH: " + machine.Layout.LauncherDirectory, lines);
        Assert.Contains("Регистрация обработана: " + UninstallerDirectory, lines);
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
            "Failed: stopping LocalAi's running tools and broker — The stop request timed out.",
            Lines(plan, journal)[0]);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Не выполнено: остановка запущенных инструментов и брокера LocalAi — The stop request timed out.",
            Lines(plan, journal)[0]);
    }

    [Fact]
    public async Task A_stop_that_never_finished_has_an_unknown_outcome()
    {
        var plan = await Plan();
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        journal.BeginStep(InstallerRunEffectKind.ProcessStop, "Ask the broker to go");

        Assert.Equal("Outcome unknown: stopping LocalAi's running tools and broker", Lines(plan, journal)[0]);
        InstallerCulture.Current = InstallerLanguage.Russian;
        Assert.Equal(
            "Результат неизвестен: остановка запущенных инструментов и брокера LocalAi",
            Lines(plan, journal)[0]);
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
        Assert.Equal("Остановлены запущенные инструменты и брокер LocalAi", lines[0]);
        AssertNoJournalDetail(lines);
    }

    private string UninstallerDirectory => new UninstallRegistration(machine.Layout).UninstallerDirectory;

    private Task<UninstallPlan> Plan() =>
        machine.PlanAsync(RemovalSelection.FromPreset(RemovalPreset.FullUninstall), TestContext.Current.CancellationToken);

    /// <summary>The real runner, so the journal holds exactly what a removal writes.</summary>
    private async Task<(UninstallPlan Plan, string[] Lines)> RemoveEverything(
        InstallerLanguage language = InstallerLanguage.English)
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
        return (plan, Lines(plan, journal));
    }

    private string[] Lines(UninstallPlan plan, InstallerRunJournal journal) =>
        UninstallReport.Capture(plan, journal.Snapshot, machine.Layout)
            .Select(RemovalReportPresentation.Line)
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
