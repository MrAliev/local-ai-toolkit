using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The PATH entry and the Apps &amp; features entry are titled by what they are rather than by a
/// bare directory, and in every state the report file and the finish page say whether the entry
/// is still there (#614). A bare "Not started: {dir}" did not say what was not started.
/// </summary>
public sealed class ThePathAndTheEntrySayWhatStaysTests : IDisposable
{
    public enum Journalled { Completed, NotStarted, Failed, Unknown, Kept }

    private const string Detail = "Access to the registry key is denied.";

    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Theory]
    [InlineData(InstallerLanguage.English, Journalled.Completed, RunMark.Done,
        "Removed from PATH: {dir}", "Removed from PATH")]
    [InlineData(InstallerLanguage.English, Journalled.NotStarted, RunMark.None,
        "Not started: removing {dir} from PATH — the entry stays", "Not started; the entry stays in PATH")]
    [InlineData(InstallerLanguage.English, Journalled.Failed, RunMark.Failed,
        "Could not remove from PATH: {dir} — {detail}", "Could not remove from PATH — {detail}")]
    [InlineData(InstallerLanguage.English, Journalled.Unknown, RunMark.Warned,
        "Outcome unknown: removing {dir} from PATH — check whether the entry is still there",
        "Outcome unknown; check whether the entry is still in PATH")]
    [InlineData(InstallerLanguage.Russian, Journalled.Completed, RunMark.Done,
        "Удалено из PATH: {dir}", "Удалено из PATH")]
    [InlineData(InstallerLanguage.Russian, Journalled.NotStarted, RunMark.None,
        "Не начато: удаление {dir} из PATH — запись остаётся", "Не начато; запись в PATH остаётся")]
    [InlineData(InstallerLanguage.Russian, Journalled.Failed, RunMark.Failed,
        "Не удалось удалить из PATH: {dir} — {detail}", "Не удалось удалить из PATH — {detail}")]
    [InlineData(InstallerLanguage.Russian, Journalled.Unknown, RunMark.Warned,
        "Результат неизвестен: удаление {dir} из PATH — проверьте, осталась ли запись",
        "Результат неизвестен; проверьте, осталась ли запись в PATH")]
    public async Task The_path_entry_says_whether_it_stays(
        InstallerLanguage language, Journalled state, RunMark mark, string line, string wording)
    {
        var directory = machine.Layout.LauncherDirectory;
        var entry = await Reported(
            RemovalReportKind.LauncherPath,
            state,
            InstallerRunEffectKind.LauncherPath,
            UninstallJournalDescriptions.LauncherPath(directory),
            language);

        Assert.Equal(Fill(line, directory), RemovalReportPresentation.Line(entry));
        Assert.Equal(
            new RunResultRow(mark, "PATH: " + directory, Fill(wording, directory)),
            RemovalReportPresentation.Row(entry));
    }

    [Theory]
    [InlineData(InstallerLanguage.English, Journalled.Completed, RunMark.Done,
        "Apps & features entry no longer listed; the uninstaller copy in {dir} is gone",
        "No longer listed; the uninstaller copy in {dir} is gone")]
    [InlineData(InstallerLanguage.English, Journalled.NotStarted, RunMark.None,
        "Not started: removing the Apps & features entry and the uninstaller copy in {dir} — the entry stays, " +
        "and the removal can be started again from there",
        "Not started; the entry stays, and the removal can be started again from Apps & features")]
    [InlineData(InstallerLanguage.English, Journalled.Failed, RunMark.Failed,
        "Could not remove completely: the Apps & features entry and the uninstaller copy in {dir} — either may " +
        "remain: {detail}",
        "Could not remove completely; the entry or the uninstaller copy in {dir} may remain — {detail}")]
    [InlineData(InstallerLanguage.English, Journalled.Unknown, RunMark.Warned,
        "Outcome unknown: removing the Apps & features entry and the uninstaller copy in {dir} — look for " +
        "Local AI Toolkit in Apps & features",
        "Outcome unknown; look for Local AI Toolkit in Apps & features")]
    [InlineData(InstallerLanguage.English, Journalled.Kept, RunMark.None,
        "Kept: the Apps & features entry and the uninstaller copy in {dir} — an earlier step failed, so the " +
        "entry stays and the removal can be started again from there",
        "Kept — an earlier step failed, so the entry stays and the removal can be started again from there")]
    [InlineData(InstallerLanguage.Russian, Journalled.Completed, RunMark.Done,
        "Запись в «Приложениях и возможностях» больше не значится; копии деинсталлятора в {dir} больше нет",
        "Больше не значится; копии деинсталлятора в {dir} больше нет")]
    [InlineData(InstallerLanguage.Russian, Journalled.NotStarted, RunMark.None,
        "Не начато: удаление записи в «Приложениях и возможностях» и копии деинсталлятора в {dir} — запись " +
        "остаётся, и удаление можно снова запустить оттуда",
        "Не начато; запись остаётся, и удаление можно снова запустить из «Приложений и возможностей»")]
    [InlineData(InstallerLanguage.Russian, Journalled.Failed, RunMark.Failed,
        "Не удалось удалить полностью: запись в «Приложениях и возможностях» и копию деинсталлятора в {dir} — " +
        "одна из них могла остаться: {detail}",
        "Не удалось удалить полностью; запись или копия деинсталлятора в {dir} могли остаться — {detail}")]
    [InlineData(InstallerLanguage.Russian, Journalled.Unknown, RunMark.Warned,
        "Результат неизвестен: удаление записи в «Приложениях и возможностях» и копии деинсталлятора в {dir} — " +
        "проверьте, есть ли там Local AI Toolkit",
        "Результат неизвестен; проверьте, есть ли Local AI Toolkit в «Приложениях и возможностях»")]
    [InlineData(InstallerLanguage.Russian, Journalled.Kept, RunMark.None,
        "Оставлено: запись в «Приложениях и возможностях» и копия деинсталлятора в {dir} — предыдущий шаг не " +
        "удался, поэтому запись остаётся и удаление можно запустить оттуда снова",
        "Оставлено — предыдущий шаг не удался, поэтому запись остаётся и удаление можно запустить оттуда снова")]
    public async Task The_apps_and_features_entry_says_whether_it_stays(
        InstallerLanguage language, Journalled state, RunMark mark, string line, string wording)
    {
        var directory = new UninstallRegistration(machine.Layout).UninstallerDirectory;
        var entry = await Reported(
            RemovalReportKind.Registration,
            state,
            InstallerRunEffectKind.RuntimeRemoval,
            state == Journalled.Kept
                ? UninstallJournalDescriptions.RegistrationKept(directory)
                : UninstallJournalDescriptions.Registration(directory),
            language);

        Assert.Equal(Fill(line, directory), RemovalReportPresentation.Line(entry));
        Assert.Equal(
            new RunResultRow(
                mark,
                language == InstallerLanguage.English ? "Apps & features entry" : "Запись в «Приложениях и возможностях»",
                Fill(wording, directory)),
            RemovalReportPresentation.Row(entry));
    }

    private static string Fill(string text, string directory) =>
        text.Replace("{dir}", directory, StringComparison.Ordinal).Replace("{detail}", Detail, StringComparison.Ordinal);

    /// <summary>The journal holds what the runner writes for the step in that state, and nothing else.</summary>
    private async Task<RemovalReportEntry> Reported(
        RemovalReportKind kind,
        Journalled state,
        InstallerRunEffectKind effect,
        string description,
        InstallerLanguage language)
    {
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            readUserPath: () => machine.Layout.LauncherDirectory) with
        {
            RemovesAppsAndFeaturesEntry = true,
        };
        Assert.True(plan.RemovesLauncherFromPath);
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        if (state != Journalled.NotStarted)
        {
            var step = journal.BeginStep(effect, description);
            if (state is Journalled.Completed or Journalled.Kept)
            {
                journal.CompleteStep(step, "Done.", isReversible: false);
            }
            else if (state == Journalled.Failed)
            {
                journal.FailStep(step, Detail);
            }
        }

        InstallerCulture.Current = language;
        return Assert.Single(
            UninstallReport.Capture(plan, journal.Snapshot, machine.Layout),
            entry => entry.Kind == kind);
    }
}
