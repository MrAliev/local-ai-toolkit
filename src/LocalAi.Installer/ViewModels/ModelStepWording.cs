using System.Globalization;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// One step of model setup, put into words — for three readers, not one.
///
/// The window speaks whatever was chosen on the start screen; the run log is written beside an
/// English journal and is English; and the model's own row already shows its tag in a column, so
/// there the phrase stands alone. The installer core used to compose the sentence itself and pick
/// the language by the process, which is how «gpt-oss:20b: проверяется, помещается ли в
/// видеопамять» ended up under "No prerequisites were selected."
/// </summary>
public static class ModelStepWording
{
    public static string ForTheRail(ModelProvisioningProgress step) =>
        step.Model + ": " + ForARow(step);

    public static string ForTheLog(ModelProvisioningProgress step) =>
        step.Model + ": " + Fill(step, English(step));

    /// <summary>The phrase alone: the row names the model in a column of its own.</summary>
    public static string ForARow(ModelProvisioningProgress step) =>
        Fill(step, InstallerCulture.Pick(English(step), Russian(step)));

    /// <summary>
    /// What of a step is worth keeping, or nothing. The log does not scroll, so a hundred byte
    /// counts per model would push everything else out of sight; a verdict is left out because
    /// the batch result carries the same news with its code and the smaller contexts that would
    /// have fitted.
    /// </summary>
    public static string? LogLine(ModelProvisioningProgress step) =>
        step.Phase is ModelProvisioningPhase.Downloading or ModelProvisioningPhase.Checking &&
        step.TotalBytes == 0
            ? ForTheLog(step)
            : null;

    /// <summary>
    /// <c>{0}</c> is either the downloaded size or a status token and <c>{1}</c> is the total
    /// size. A template that uses neither simply ignores them.
    /// </summary>
    private static string Fill(ModelProvisioningProgress step, string template) =>
        string.Format(
            CultureInfo.InvariantCulture,
            template,
            step.Phase == ModelProvisioningPhase.Other
                ? step.OllamaStatus ?? string.Empty
                : Gigabytes(step.CompletedBytes),
            Gigabytes(step.TotalBytes));

    private static string English(ModelProvisioningProgress step) => step.Phase switch
    {
        ModelProvisioningPhase.Planned => "waiting",
        ModelProvisioningPhase.Downloading when step.TotalBytes > 0 => "{0} of {1} GB downloaded",
        ModelProvisioningPhase.Downloading => "downloading",
        ModelProvisioningPhase.Verifying => "checking the download",
        ModelProvisioningPhase.Storing => "adding to Ollama",
        ModelProvisioningPhase.Other => "Ollama reports {0}",
        ModelProvisioningPhase.Checking => "checking it fits in video memory",
        ModelProvisioningPhase.Accepted when step.ResidencyDegraded => "ready with limitations",
        ModelProvisioningPhase.Accepted => "ready",
        ModelProvisioningPhase.Rejected => "does not fit, so it is not used",
        ModelProvisioningPhase.TrialLoadFailed => "trial load failed",
        _ => string.Empty,
    };

    private static string Russian(ModelProvisioningProgress step) => step.Phase switch
    {
        ModelProvisioningPhase.Planned => "ожидает",
        ModelProvisioningPhase.Downloading when step.TotalBytes > 0 => "скачано {0} из {1} ГБ",
        ModelProvisioningPhase.Downloading => "скачивается",
        ModelProvisioningPhase.Verifying => "проверяется скачанное",
        ModelProvisioningPhase.Storing => "добавляется в Ollama",
        ModelProvisioningPhase.Other => "Ollama сообщает {0}",
        ModelProvisioningPhase.Checking => "проверяется, помещается ли в видеопамять",
        ModelProvisioningPhase.Accepted when step.ResidencyDegraded => "готова с ограничениями",
        ModelProvisioningPhase.Accepted => "готова",
        ModelProvisioningPhase.Rejected => "не помещается, поэтому не используется",
        ModelProvisioningPhase.TrialLoadFailed => "пробная загрузка не удалась",
        _ => string.Empty,
    };

    /// <summary>
    /// Gibibytes to one decimal, invariant — the same figure the console prints, so the window
    /// and a terminal beside it do not disagree about the size of one download.
    /// </summary>
    private static string Gigabytes(long bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
