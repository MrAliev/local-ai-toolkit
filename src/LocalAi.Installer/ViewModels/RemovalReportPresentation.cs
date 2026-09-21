using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

public static class RemovalReportPresentation
{
    public static RunResultRow Row(RemovalReportEntry entry) => new(
        entry.State switch
        {
            RemovalReportState.Completed => RunMark.Done,
            RemovalReportState.Failed => RunMark.Failed,
            RemovalReportState.Unknown => RunMark.Warned,
            _ => RunMark.None,
        }, entry.Target, Wording(entry));

    public static string Line(RemovalReportEntry entry) => Prefix(entry) + entry.Target +
        (string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : " — " + entry.Detail);

    private static string Wording(RemovalReportEntry entry) => Prefix(entry).TrimEnd(' ', ':') +
        (string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : " — " + entry.Detail);

    private static string Prefix(RemovalReportEntry entry) => entry.State switch
    {
        RemovalReportState.NotStarted => InstallerCulture.Pick("Not started: ", "Не начато: "),
        RemovalReportState.Retained => InstallerCulture.Pick("Kept: ", "Оставлено: "),
        RemovalReportState.Unknown => InstallerCulture.Pick("Outcome unknown: ", "Результат неизвестен: "),
        RemovalReportState.Failed => InstallerCulture.Pick(
            "Could not remove (partial changes possible): ", "Не удалось удалить (возможны частичные изменения): "),
        _ => entry.Kind switch
        {
            RemovalReportKind.Path => InstallerCulture.Pick("Removed ", "Удалено "),
            RemovalReportKind.Configuration => InstallerCulture.Pick("Updated ", "Обновлено "),
            RemovalReportKind.Hooks => InstallerCulture.Pick("Removed hook ", "Удалён хук "),
            RemovalReportKind.Registration => InstallerCulture.Pick("Registration processed: ", "Регистрация обработана: "),
            RemovalReportKind.LauncherPath => InstallerCulture.Pick("Removed from PATH: ", "Удалено из PATH: "),
            _ => InstallerCulture.Pick("Completed: ", "Выполнено: "),
        },
    };
}
