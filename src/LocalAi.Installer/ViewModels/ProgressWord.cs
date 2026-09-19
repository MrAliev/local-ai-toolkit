using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// The fixed states the progress line can rest in. Held as a state rather than as its words, so
/// a language chosen while the page shows one can reword it; what a step says while it runs is
/// replaced by the next step and is kept as text.
/// </summary>
public enum ProgressWord
{
    Ready,
    Completed,
    FinishedWithProblems,
    Failed,
    Cancelled,
    Refused,
}

public static class ProgressWords
{
    public static string Text(ProgressWord word) => word switch
    {
        ProgressWord.Ready => InstallerCulture.Pick("Ready", "Готово"),
        ProgressWord.Completed => InstallerCulture.Pick("Completed", "Завершено"),
        ProgressWord.FinishedWithProblems => InstallerCulture.Pick(
            "Finished with problems",
            "Завершено с проблемами"),
        ProgressWord.Failed => InstallerCulture.Pick("Failed", "Ошибка"),
        ProgressWord.Cancelled => InstallerCulture.Pick("Cancelled", "Отменено"),
        ProgressWord.Refused => InstallerCulture.Pick("Refused", "Отказано"),
        _ => string.Empty,
    };
}
