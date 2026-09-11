using System.Globalization;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// One step of model setup, put into words — twice, because it has two readers.
///
/// The window speaks whatever was chosen on the start screen; the run log is written beside an
/// English journal and is English. The installer core used to compose the sentence itself and
/// pick the language by the process, so a Russian window wrote «gpt-oss:20b: проверяется,
/// помещается ли в видеопамять» into a log that reads "No prerequisites were selected." two
/// lines above.
/// </summary>
public static class ModelStepWording
{
    public static string ForTheRail(ModelProvisioningProgress step) =>
        Fill(step, InstallerCulture.Pick(English(step), Russian(step)));

    public static string ForTheLog(ModelProvisioningProgress step) => Fill(step, English(step));

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
    /// <c>{0}</c> is the model, <c>{1}</c> is either the downloaded size or a status token, and
    /// <c>{2}</c> is the total size. A template that uses none of them simply ignores them.
    /// </summary>
    private static string Fill(ModelProvisioningProgress step, string template) =>
        string.Format(
            CultureInfo.InvariantCulture,
            template,
            step.Model,
            step.Phase == ModelProvisioningPhase.Other
                ? step.OllamaStatus ?? string.Empty
                : Gigabytes(step.CompletedBytes),
            Gigabytes(step.TotalBytes));

    private static string English(ModelProvisioningProgress step) => step.Phase switch
    {
        ModelProvisioningPhase.Planned => "{0}: waiting",
        ModelProvisioningPhase.Downloading when step.TotalBytes > 0 => "{0}: {1} of {2} GB",
        ModelProvisioningPhase.Downloading => "{0}: downloading",
        ModelProvisioningPhase.Verifying => "{0}: checking the download",
        ModelProvisioningPhase.Storing => "{0}: adding to Ollama",
        ModelProvisioningPhase.Other => "{0}: Ollama reports {1}",
        ModelProvisioningPhase.Checking => "{0}: checking it fits in video memory",
        ModelProvisioningPhase.Accepted => "{0}: ready",
        ModelProvisioningPhase.Rejected => "{0}: does not fit, so it is not used",
        _ => "{0}",
    };

    private static string Russian(ModelProvisioningProgress step) => step.Phase switch
    {
        ModelProvisioningPhase.Planned => "{0}: ожидает",
        ModelProvisioningPhase.Downloading when step.TotalBytes > 0 => "{0}: {1} из {2} ГБ",
        ModelProvisioningPhase.Downloading => "{0}: скачивается",
        ModelProvisioningPhase.Verifying => "{0}: проверяется скачанное",
        ModelProvisioningPhase.Storing => "{0}: добавляется в Ollama",
        ModelProvisioningPhase.Other => "{0}: Ollama сообщает {1}",
        ModelProvisioningPhase.Checking => "{0}: проверяется, помещается ли в видеопамять",
        ModelProvisioningPhase.Accepted => "{0}: готова",
        ModelProvisioningPhase.Rejected => "{0}: не помещается, поэтому не используется",
        _ => "{0}",
    };

    /// <summary>
    /// Gibibytes to one decimal, invariant — the same figure the console prints, so the window
    /// and a terminal beside it do not disagree about the size of one download.
    /// </summary>
    private static string Gigabytes(long bytes) =>
        (bytes / (1024d * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
