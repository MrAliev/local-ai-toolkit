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

    public static string Line(RemovalReportEntry entry) =>
        WhatStays(entry)?.Line ??
        (Deleted(entry) is { Length: > 0 } deleted ? Disconnection(entry, deleted) : Prefix(entry) + Target(entry)) +
        Suffix(entry);

    // A completed client step that deleted a file says so, in the verb the review page used for it.
    private static RemovalReportFile[] Deleted(RemovalReportEntry entry) =>
        entry is { State: RemovalReportState.Completed, Kind: RemovalReportKind.Configuration, Files: { } files }
            ? files.Where(file => file.Deletes).ToArray()
            : [];

    private static string Disconnection(RemovalReportEntry entry, RemovalReportFile[] deleted)
    {
        var rewritten = string.Join(", ", entry.Files!.Where(file => !file.Deletes).Select(file => file.Path));
        var removed = string.Join(", ", deleted.Select(file => file.Path));
        return rewritten.Length > 0
            ? string.Format(InstallerCulture.Pick("Updated {0}; removed {1}", "Обновлено {0}; удалено {1}"), rewritten, removed)
            : string.Format(InstallerCulture.Pick("Removed {0}", "Удалено {0}"), removed);
    }

    // A row's title names what the step was about; the line under it says what happened.
    private static string Title(RemovalReportEntry entry) => entry.Kind switch
    {
        RemovalReportKind.ProcessStop => InstallerCulture.Pick(
            "LocalAi's running tools and broker", "Запущенные инструменты и брокер LocalAi"),
        RemovalReportKind.LauncherPath => "PATH: " + entry.Target,
        RemovalReportKind.Registration => InstallerCulture.Pick(
            "Apps & features entry", "Запись в «Приложениях и возможностях»"),
        _ => Target(entry),
    };

    // The PATH entry and the Apps & features entry say in every state whether the entry stays.
    private static (string Line, string Wording)? WhatStays(RemovalReportEntry entry) =>
        (entry.Kind, entry.State) switch
        {
            (RemovalReportKind.LauncherPath, RemovalReportState.Completed) => (
                Say(entry, "Removed from PATH: {0}", "Удалено из PATH: {0}"),
                Say(entry, "Removed from PATH", "Удалено из PATH")),
            (RemovalReportKind.LauncherPath, RemovalReportState.NotStarted) => (
                Say(entry, "Not started: removing {0} from PATH — the entry stays",
                    "Не начато: удаление {0} из PATH — запись остаётся"),
                Say(entry, "Not started; the entry stays in PATH", "Не начато; запись в PATH остаётся")),
            (RemovalReportKind.LauncherPath, RemovalReportState.Failed) => (
                Say(entry, "Could not remove from PATH: {0} — {1}", "Не удалось удалить из PATH: {0} — {1}"),
                Say(entry, "Could not remove from PATH — {1}", "Не удалось удалить из PATH — {1}")),
            (RemovalReportKind.LauncherPath, RemovalReportState.Unknown) => (
                Say(entry, "Outcome unknown: removing {0} from PATH — check whether the entry is still there",
                    "Результат неизвестен: удаление {0} из PATH — проверьте, осталась ли запись"),
                Say(entry, "Outcome unknown; check whether the entry is still in PATH",
                    "Результат неизвестен; проверьте, осталась ли запись в PATH")),
            (RemovalReportKind.Registration, RemovalReportState.Completed) => (
                Say(entry, "Apps & features entry no longer listed; the uninstaller copy in {0} is gone",
                    "Запись в «Приложениях и возможностях» больше не значится; копии деинсталлятора в {0} больше нет"),
                Say(entry, "No longer listed; the uninstaller copy in {0} is gone",
                    "Больше не значится; копии деинсталлятора в {0} больше нет")),
            (RemovalReportKind.Registration, RemovalReportState.NotStarted) => (
                Say(entry,
                    "Not started: removing the Apps & features entry and the uninstaller copy in {0} — the entry " +
                    "stays, and the removal can be started again from there",
                    "Не начато: удаление записи в «Приложениях и возможностях» и копии деинсталлятора в {0} — " +
                    "запись остаётся, и удаление можно снова запустить оттуда"),
                Say(entry, "Not started; the entry stays, and the removal can be started again from Apps & features",
                    "Не начато; запись остаётся, и удаление можно снова запустить из «Приложений и возможностей»")),
            (RemovalReportKind.Registration, RemovalReportState.Failed) => (
                Say(entry,
                    "Could not remove completely: the Apps & features entry and the uninstaller copy in {0} — " +
                    "either may remain: {1}",
                    "Не удалось удалить полностью: запись в «Приложениях и возможностях» и копию деинсталлятора " +
                    "в {0} — одна из них могла остаться: {1}"),
                Say(entry, "Could not remove completely; the entry or the uninstaller copy in {0} may remain — {1}",
                    "Не удалось удалить полностью; запись или копия деинсталлятора в {0} могли остаться — {1}")),
            (RemovalReportKind.Registration, RemovalReportState.Unknown) => (
                Say(entry,
                    "Outcome unknown: removing the Apps & features entry and the uninstaller copy in {0} — look " +
                    "for {2} in Apps & features",
                    "Результат неизвестен: удаление записи в «Приложениях и возможностях» и копии деинсталлятора " +
                    "в {0} — проверьте, есть ли там {2}"),
                Say(entry, "Outcome unknown; look for {2} in Apps & features",
                    "Результат неизвестен; проверьте, есть ли {2} в «Приложениях и возможностях»")),
            (RemovalReportKind.Registration, RemovalReportState.Retained) => (
                Say(entry, "Kept: the Apps & features entry and the uninstaller copy in {0} — {1}",
                    "Оставлено: запись в «Приложениях и возможностях» и копия деинсталлятора в {0} — {1}"),
                Say(entry, "Kept — {1}", "Оставлено — {1}")),
            _ => null,
        };

    // {0} is the launcher directory or the one holding the uninstaller copy, {1} the detail, {2}
    // the name Apps & features lists.
    private static string Say(RemovalReportEntry entry, string english, string russian) => string.Format(
        InstallerCulture.Pick(english, russian), entry.Target, entry.Detail, UninstallRegistration.DisplayName);

    private static string Wording(RemovalReportEntry entry) => WhatStays(entry)?.Wording ?? (entry.State, entry.Kind) switch
    {
        (RemovalReportState.Completed, RemovalReportKind.ProcessStop) => InstallerCulture.Pick("Stopped", "Остановлены"),
        (RemovalReportState.Completed, RemovalReportKind.Hooks) => InstallerCulture.Pick("LocalAi hooks removed", "Хуки LocalAi удалены"),
        // The title carries the full paths already, and names only removed files when nothing was rewritten.
        _ when Deleted(entry) is { Length: > 0 } deleted => deleted.Length == entry.Files!.Count
            ? InstallerCulture.Pick("Removed", "Удалено")
            : string.Format(
                InstallerCulture.Pick("Updated; removed {0}", "Обновлено; удалено {0}"),
                string.Join(", ", deleted.Select(file => System.IO.Path.GetFileName(file.Path)))),
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
            RemovalReportKind.ProcessStop => string.Empty,
            _ => InstallerCulture.Pick("Completed: ", "Выполнено: "),
        },
    };
}
