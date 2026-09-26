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
        }, Target(entry), Wording(entry));

    public static string Line(RemovalReportEntry entry) => Prefix(entry) + Target(entry) + Suffix(entry);

    private static string Wording(RemovalReportEntry entry) => Prefix(entry).TrimEnd(' ', ':') + Suffix(entry);

    private static string Suffix(RemovalReportEntry entry) =>
        Detail(entry) is { } detail && !string.IsNullOrWhiteSpace(detail) ? " — " + detail : string.Empty;

    // "Updated" does not say what changed, so a configuration says it here.
    private static string? Detail(RemovalReportEntry entry) => entry.State != RemovalReportState.Completed
        ? entry.Detail
        : entry.Kind == RemovalReportKind.Configuration
            ? InstallerCulture.Pick("LocalAi disconnected", "LocalAi отключён")
            : null;

    // The stop's own target is the journal's English description.
    private static string Target(RemovalReportEntry entry) => entry.Kind != RemovalReportKind.ProcessStop
        ? entry.Target
        : entry.State == RemovalReportState.Completed
            ? InstallerCulture.Pick(
                "Stopped LocalAi's running tools and broker",
                "Остановлены запущенные инструменты и брокер LocalAi")
            : InstallerCulture.Pick(
                "stopping LocalAi's running tools and broker",
                "остановка запущенных инструментов и брокера LocalAi");

    private static string Prefix(RemovalReportEntry entry) => entry.State switch
    {
        RemovalReportState.NotStarted => InstallerCulture.Pick("Not started: ", "Не начато: "),
        RemovalReportState.Retained => InstallerCulture.Pick("Kept: ", "Оставлено: "),
        RemovalReportState.Unknown => InstallerCulture.Pick("Outcome unknown: ", "Результат неизвестен: "),
        // The stop removes nothing. Other is the empty runtime root and repeats of a configuration
        // or hook failure whose own line already carries the partial-changes caveat.
        RemovalReportState.Failed when entry.Kind is RemovalReportKind.Other or RemovalReportKind.ProcessStop =>
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
