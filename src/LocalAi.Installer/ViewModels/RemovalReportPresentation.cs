using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

/// <summary>A completed step is worded by its kind; the journal's English detail stays in the journal.</summary>
public static class RemovalReportPresentation
{
    public static RunResultRow Row(RemovalReportEntry entry) => new(
        entry.State switch
        {
            RemovalReportState.Completed => RunMark.Done,
            RemovalReportState.Failed => RunMark.Failed,
            RemovalReportState.Unknown => RunMark.Warned,
            _ => RunMark.None,
        }, Title(entry), Wording(entry));

    public static string Line(RemovalReportEntry entry) => Prefix(entry) + Target(entry) + Suffix(entry);

    // A row's title names what the step was about; the line under it says what happened.
    private static string Title(RemovalReportEntry entry) => entry.Kind == RemovalReportKind.ProcessStop
        ? InstallerCulture.Pick("LocalAi's running tools and broker", "Запущенные инструменты и брокер LocalAi")
        : Target(entry);

    private static string Wording(RemovalReportEntry entry) => (entry.State, entry.Kind) switch
    {
        (RemovalReportState.Completed, RemovalReportKind.ProcessStop) => InstallerCulture.Pick("Stopped", "Остановлены"),
        (RemovalReportState.Completed, RemovalReportKind.Hooks) => InstallerCulture.Pick("LocalAi hooks removed", "Хуки LocalAi удалены"),
        _ => Prefix(entry).TrimEnd(' ', ':'),
    } + Suffix(entry);

    private static string Suffix(RemovalReportEntry entry) =>
        Detail(entry) is { } detail && !string.IsNullOrWhiteSpace(detail) ? " — " + detail : string.Empty;

    // "Updated" does not say what changed, so a configuration says it here.
    private static string? Detail(RemovalReportEntry entry) => entry.State != RemovalReportState.Completed
        ? entry.Detail
        : entry.Kind == RemovalReportKind.Configuration
            ? InstallerCulture.Pick("LocalAi disconnected", "LocalAi отключён")
            : null;

    // A journal step's target is the journal's English description: the stop's is replaced, and
    // one of a kind with no wording of its own is marked as the journal's.
    private static string Target(RemovalReportEntry entry) => entry.Kind switch
    {
        RemovalReportKind.ProcessStop when entry.State == RemovalReportState.Completed => InstallerCulture.Pick(
            "Stopped LocalAi's running tools and broker",
            "Остановлены запущенные инструменты и брокер LocalAi"),
        RemovalReportKind.ProcessStop => InstallerCulture.Pick(
            "stopping LocalAi's running tools and broker",
            "остановка запущенных инструментов и брокера LocalAi"),
        RemovalReportKind.JournalStep => entry.Target + InstallerCulture.Pick(" (journal step)", " (шаг журнала, на английском)"),
        _ => entry.Target,
    };

    private static string Prefix(RemovalReportEntry entry) => entry.State switch
    {
        RemovalReportState.NotStarted => InstallerCulture.Pick("Not started: ", "Не начато: "),
        RemovalReportState.Retained => InstallerCulture.Pick("Kept: ", "Оставлено: "),
        RemovalReportState.Unknown => InstallerCulture.Pick("Outcome unknown: ", "Результат неизвестен: "),
        // Neither the stop nor an unknown journal step is a removal. Other is the empty runtime
        // root and repeats of a configuration or hook failure whose own line already carries the
        // partial-changes caveat.
        RemovalReportState.Failed when entry.Kind is
            RemovalReportKind.Other or RemovalReportKind.ProcessStop or RemovalReportKind.JournalStep =>
            InstallerCulture.Pick("Failed: ", "Не выполнено: "),
        RemovalReportState.Failed => InstallerCulture.Pick(
            "Could not remove (partial changes possible): ", "Не удалось удалить (возможны частичные изменения): "),
        _ => entry.Kind switch
        {
            RemovalReportKind.Path => InstallerCulture.Pick("Removed ", "Удалено "),
            RemovalReportKind.Configuration => InstallerCulture.Pick("Updated ", "Обновлено "),
            RemovalReportKind.Hooks => InstallerCulture.Pick("Removed LocalAi hooks from ", "Удалены хуки LocalAi из "),
            RemovalReportKind.Registration => InstallerCulture.Pick("Registration processed: ", "Регистрация обработана: "),
            RemovalReportKind.LauncherPath => InstallerCulture.Pick("Removed from PATH: ", "Удалено из PATH: "),
            RemovalReportKind.ProcessStop => string.Empty,
            _ => InstallerCulture.Pick("Completed: ", "Выполнено: "),
        },
    };
}
