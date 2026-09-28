using System.Globalization;
using LocalAi.Contracts;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Models;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.ViewModels;

public enum PackageOutcomeKind
{
    /// <summary>No verified release was selected, so nothing was attempted.</summary>
    NoRelease,

    /// <summary>The installation layout refused it; <see cref="PackageOutcome.Reason"/> says why.</summary>
    Refused,

    /// <summary>The package installer answered with a status.</summary>
    Result,

    /// <summary>
    /// The package step stopped with an error other than a layout refusal; <see cref="PackageOutcome.Failure"/>
    /// names it when it is one the row words by its cause.
    /// </summary>
    Failed,

    /// <summary>
    /// The run was cancelled after the package step began and before activation;
    /// <see cref="PackageOutcome.StillInstalled"/> names the release installed after it.
    /// </summary>
    Cancelled,
}

/// <summary>Why the package was not installed, by what the reader does next.</summary>
public enum PackageFailureKind
{
    /// <summary>Its size or hash is not the manifest's beside it in the folder.</summary>
    FolderCopyDoesNotMatch,

    /// <summary>Its size or hash is not the manifest's it was downloaded against.</summary>
    DownloadDoesNotMatch,

    /// <summary>It matched its manifest and a check on its contents refused it.</summary>
    ContentsRejected,

    NotInFolder,
    NoReadAccess,

    /// <summary>It could not be opened for another reason; the detail names it.</summary>
    Unreadable,

    TooLarge,
    NotDownloaded,

    /// <summary>The installer's working copy could not be written; the detail says why.</summary>
    NotWritten,
}

/// <summary>What the package step ended in, kept as facts so its row can be worded in any language.</summary>
public sealed record PackageOutcome(
    PackageOutcomeKind Kind,
    LocalAiPackageInstallStatus Status = default,
    string? Version = null,
    string? Reason = null,
    IReadOnlyList<string>? Restored = null,
    string? PriorVersion = null,
    PackageFailureKind? Failure = null,
    string? StillInstalled = null)
{
    /// <summary>What the package installer's answer amounts to.</summary>
    public static PackageOutcome FromResult(ReleaseInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            PackageOutcomeKind.Result,
            result.Status,
            result.Version,
            result.Reason,
            result.RestoredFiles,
            result.PriorVersion);
    }

    /// <summary>What an installation exception from the package installer amounts to.</summary>
    public static PackageOutcome FromInstallationFailure(string message) =>
        new(
            InstallationFailureAdvice.IsLayoutFailure(message)
                ? PackageOutcomeKind.Refused
                : PackageOutcomeKind.Failed,
            Reason: message);
}

public enum ModelStepFailureKind
{
    /// <summary>The batch stopped early; the detail is its status token.</summary>
    Stopped,
    Cancelled,

    /// <summary>Models were asked for and none was requested; the detail is the reasons.</summary>
    NothingRequested,
}

/// <summary>
/// How model setup ended when it did not finish. A batch that stops before its first model
/// leaves no row to say so, and the summary called that "not attempted" while the run failed on
/// it. <see cref="Detail"/> comes from the run and is never translated.
/// </summary>
public sealed record ModelStepFailure(ModelStepFailureKind Kind, string Detail)
{
    public static ModelStepFailure? Of(ReleaseModelInstallReport? models) =>
        models is null
            ? null
            : models.Batch is { } batch
                ? Of(batch.StopReason, batch.Code)
                : new(
                    ModelStepFailureKind.NothingRequested,
                    models.Excluded.Count == 0
                        ? "The installer produced no model request."
                        : string.Join(" ", models.Excluded));

    internal static ModelStepFailure? Of(BrokerModelBatchStopReason stopReason, string code) =>
        stopReason switch
        {
            BrokerModelBatchStopReason.None => null,
            BrokerModelBatchStopReason.Cancelled => new(ModelStepFailureKind.Cancelled, code),
            _ => new(ModelStepFailureKind.Stopped, code),
        };
}

public enum PathOutcome
{
    Added,
    AlreadyThere,
    Failed,
}

public enum ClientRunState
{
    NotAttempted,
    Configuring,
    Applied,
    AlreadyConfigured,
    Failed,

    /// <summary>Not attempted because the package was not installed.</summary>
    Skipped,
}

public sealed record ClientRunOutcome(string Name, ClientRunState State, string? Reason = null);

public enum PrerequisiteOutcomeKind
{
    Installed,
    Reinstalled,
    NoAutomatedInstaller,
    WinGetNotFound,
    WinGetRefused,
    ExitCode,
    TimedOut,
    CouldNotStart,
    NpmNotFound,
    CheckAfterInstallFailed,
    Cancelled,

    /// <summary>winget or npm outlived its stop and may still be running; the run ended there.</summary>
    CouldNotStop,

    /// <summary>npm installed it, but a check afterwards outlived its stop; the run ended there.</summary>
    CheckCouldNotStop,

    /// <summary>The run stopped before this one.</summary>
    NotReached,
}

/// <summary>
/// What became of one prerequisite, kept as facts so its row is worded in the language on screen.
/// <see cref="Tool"/> is <c>winget</c> or <c>npm</c> and <see cref="Reason"/> an exception's own
/// message, neither translated. <see cref="LogLine"/> is the English line for the log and journal.
/// </summary>
public sealed record PrerequisiteOutcome(
    string Title,
    PrerequisiteOutcomeKind Kind,
    Uri? Download = null,
    string? Tool = null,
    int? ExitCode = null,
    int? Minutes = null,
    string? Reason = null,
    string? LogLine = null)
{
    public bool Succeeded => Kind is PrerequisiteOutcomeKind.Installed or PrerequisiteOutcomeKind.Reinstalled;
}

public enum RegistrationOutcome
{
    Registered,
    Missing,
    Failed,
}

/// <summary>
/// What a run did, one row per thing it touched.
///
/// The page it ends on used to be four counters and a log. On the ordinary run — where nobody
/// ticks a prerequisite because the machine already has them — the counters read "0. 0. 0. 0."
/// beside a package that had either installed or not, and neither the counters nor the log said
/// which. A tally is a thing to keep; it is not a thing to open a page with.
///
/// Built here rather than inside the wizard so each row can be read on its own, without a run.
/// </summary>
public static class RunResults
{
    public static RunResultRow Package(string? outcome, bool installed) =>
        new(
            outcome is null ? RunMark.None : installed ? RunMark.Done : RunMark.Failed,
            PageLabels.ResultPackage,
            outcome ?? PageLabels.ResultNotAttempted);

    /// <summary>One prerequisite's row. The link goes last, without a period: it is copied.</summary>
    public static RunResultRow Prerequisite(PrerequisiteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var mark = outcome.Kind switch
        {
            PrerequisiteOutcomeKind.Installed or PrerequisiteOutcomeKind.Reinstalled => RunMark.Done,
            PrerequisiteOutcomeKind.NoAutomatedInstaller or PrerequisiteOutcomeKind.NotReached => RunMark.None,
            PrerequisiteOutcomeKind.CouldNotStop or PrerequisiteOutcomeKind.CheckCouldNotStop => RunMark.Warned,
            _ => RunMark.Failed,
        };
        return new(mark, outcome.Title, outcome.Kind switch
        {
            PrerequisiteOutcomeKind.Installed => InstallerCulture.Pick("installed", "установлен"),
            PrerequisiteOutcomeKind.Reinstalled => InstallerCulture.Pick("reinstalled", "переустановлен"),
            PrerequisiteOutcomeKind.WinGetNotFound => Format(
                "not installed: WinGet was not found. Install it yourself: {0}",
                "не установлен: WinGet не найден. Установите вручную: {0}",
                outcome.Download),
            PrerequisiteOutcomeKind.WinGetRefused => Format(
                "not installed: winget on this computer failed the installer's safety check. The log " +
                "below says why and what to do; you can also install it yourself: {0}",
                "не установлен: winget на этом компьютере не прошёл проверку безопасности " +
                "установщика. Почему и что делать — сказано в журнале ниже; можно также установить " +
                "вручную: {0}",
                outcome.Download),
            PrerequisiteOutcomeKind.ExitCode => Format(
                "not installed: {0} ended with exit code {1}. Install it yourself: {2}",
                "не установлен: {0} завершился с кодом {1}. Установите вручную: {2}",
                outcome.Tool, outcome.ExitCode, outcome.Download),
            PrerequisiteOutcomeKind.TimedOut => Format(
                "not installed: {0} did not finish within {1} min and was stopped. Install it yourself: {2}",
                "не установлен: {0} не завершился за {1} мин и был остановлен. Установите вручную: {2}",
                outcome.Tool, outcome.Minutes, outcome.Download),
            PrerequisiteOutcomeKind.CouldNotStart => Format(
                "not installed: {0} could not be started ({1}). Install it yourself: {2}",
                "не установлен: не удалось запустить {0} ({1}). Установите вручную: {2}",
                outcome.Tool, outcome.Reason, outcome.Download),
            PrerequisiteOutcomeKind.NpmNotFound => InstallerCulture.Pick(
                "not installed: npm was not found. With Node.js 20 installed, run this installer again",
                "не установлен: npm не найден. Когда Node.js 20 будет установлен, снова запустите установщик"),
            PrerequisiteOutcomeKind.CheckAfterInstallFailed => InstallerCulture.Pick(
                "installed by npm, but a step after installing failed. The log below says which and why",
                "npm установил пакет, но один из шагов после установки не удался. Какой и почему — " +
                "сказано в журнале ниже"),
            PrerequisiteOutcomeKind.Cancelled => InstallerCulture.Pick(
                "not installed: the run was cancelled",
                "не установлен: запуск отменён"),
            PrerequisiteOutcomeKind.CouldNotStop => Format(
                "outcome unknown: {0} could not be stopped, so the installation may still be running. " +
                "Wait for it to end before running this installer again",
                "результат неизвестен: не удалось остановить {0}, поэтому установка, возможно, ещё идёт. " +
                "Дождитесь её окончания, прежде чем снова запускать установщик",
                outcome.Tool),
            PrerequisiteOutcomeKind.CheckCouldNotStop => InstallerCulture.Pick(
                "outcome unknown: npm installed it, but a check afterwards could not be stopped and may still " +
                "be running, so whether it works is not known. Reinstalling it with this installer repeats the check",
                "результат неизвестен: npm установил пакет, но проверку после установки не удалось остановить, " +
                "и она, возможно, ещё идёт, поэтому неизвестно, работает ли он. Переустановка этим установщиком " +
                "повторит проверку"),
            PrerequisiteOutcomeKind.NoAutomatedInstaller => InstallerCulture.Pick(
                "skipped: there is no automated installer for it",
                "пропущено: автоматической установки нет"),
            _ => PageLabels.ResultNotAttempted,
        });

        static string Format(string english, string russian, params object?[] values) =>
            string.Format(CultureInfo.InvariantCulture, InstallerCulture.Pick(english, russian), values);
    }

    public static RunResultRow Prerequisites(
        int requested,
        int installed,
        int failed,
        IReadOnlyList<string>? stillNeeded = null) =>
        new(
            requested == 0
                ? stillNeeded is { Count: > 0 } ? RunMark.Warned : RunMark.None
                : failed > 0 ? RunMark.Failed :
                installed < requested ? RunMark.Warned : RunMark.Done,
            PageLabels.ResultPrerequisites,
            requested == 0
                ? stillNeeded is { Count: > 0 }
                    ? string.Format(
                        InstallerCulture.Pick(
                            "nothing was selected; still needed, install by hand: {0}",
                            "ничего не выбрано; ещё нужны, установить вручную: {0}"),
                        string.Join(", ", stillNeeded))
                    : PageLabels.ResultNothingSelected
                : installed + failed < requested
                    ? string.Format(CultureInfo.InvariantCulture,
                        InstallerCulture.Pick(
                            "installed {0} of {1}, failed {2}, not completed: {3}",
                            "установлено {0} из {1}, не удалось {2}, не завершено: {3}"),
                        installed, requested, failed, requested - installed - failed)
                : failed == 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        PageLabels.ResultInstalledCount,
                        installed,
                        requested)
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        PageLabels.ResultInstalledAndFailed,
                        installed,
                        requested,
                        failed));

    /// <summary>
    /// The count, marked by the worst thing that happened to any of them. Which model did what is
    /// already on the install page, row by row. A step that did not finish is marked by that
    /// too, and says how it ended when no model got as far as a row. Models the run skipped say
    /// so rather than "not attempted", which is left for models chosen and not reached.
    /// </summary>
    public static RunResultRow Models(
        IReadOnlyList<ModelRunRow> models,
        ModelStepFailure? failure = null,
        bool skipped = false)
    {
        ArgumentNullException.ThrowIfNull(models);
        var limited = models.Count(model => model.State == ModelRunState.ReadyWithLimitations);
        var ready = models.Count(model => model.State == ModelRunState.Ready) + limited;
        var mark = failure is { Kind: not ModelStepFailureKind.Cancelled }
            ? RunMark.Failed
            : models.Count == 0
            ? RunMark.None
            : models.Any(model => model.State == ModelRunState.Failed)
                ? RunMark.Failed
                : ready == models.Count && limited == 0
                    ? RunMark.Done
                    : models.All(model => model.State is
                        ModelRunState.Waiting or ModelRunState.NotReached or ModelRunState.Cancelled)
                        ? RunMark.None
                        : RunMark.Warned;
        var details = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, PageLabels.ResultModelsReady, ready, models.Count),
        };
        AddCount(ModelRunState.ReadyWithLimitations, PageLabels.ResultModelsLimited);
        AddCount(ModelRunState.Rejected, PageLabels.ResultModelsRejected);
        AddCount(ModelRunState.Failed, PageLabels.ResultModelsFailed);
        AddCount(ModelRunState.Cancelled, PageLabels.ResultModelsCancelled);
        AddCount(ModelRunState.Unknown, PageLabels.ResultModelsUnknown);
        AddCount(ModelRunState.Working, PageLabels.ResultModelsWorking);
        var notValidated = models.Count(model => model.State is
            ModelRunState.Waiting or ModelRunState.NotReached);
        if (notValidated > 0)
        {
            details.Add(string.Format(
                CultureInfo.InvariantCulture, PageLabels.ResultModelsNotValidated, notValidated));
        }

        return new(
            mark,
            PageLabels.LocalModels,
            models.Count > 0
                ? string.Join("; ", details)
                : failure?.Kind switch
                {
                    null => skipped ? PageLabels.ResultModelsSkipped : PageLabels.ResultNotAttempted,
                    ModelStepFailureKind.Stopped => string.Format(
                        CultureInfo.InvariantCulture, PageLabels.ModelFailed, failure.Detail),
                    ModelStepFailureKind.Cancelled => PageLabels.ResultModelsCancelledBeforeFirst,
                    _ => PageLabels.ResultModelsNoneRequested,
                });

        void AddCount(ModelRunState state, string wording)
        {
            var count = models.Count(model => model.State == state);
            if (count > 0)
            {
                details.Add(string.Format(CultureInfo.InvariantCulture, wording, count));
            }
        }
    }

    public static RunResultRow Clients(IReadOnlyList<string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return new(
            configured.Count == 0 ? RunMark.None : RunMark.Done,
            PageLabels.ResultClients,
            configured.Count == 0
                ? PageLabels.ResultLeftUnchanged
                : string.Join(", ", configured));
    }

    /// <summary>The memory rule the run was asked to store and could not, named as the review named it.</summary>
    public static RunResultRow ResidencyNotStored(ModelResidencyPolicy policy, string reason) => new(
        RunMark.Failed,
        PageLabels.VideoMemory,
        string.Format(
            InstallerCulture.Pick(
                "the setting “{0}” was not saved. Technical details: {1}",
                "правило «{0}» не сохранено. Технические сведения: {1}"),
            ResidencyPageViewModel.Name(policy),
            reason));

    public static RunResultRow PathEntry(RunMark mark, string detail) =>
        new(mark, PageLabels.ResultPath, detail);

    public static RunResultRow Client(ClientRunOutcome outcome) => new(
        outcome.State switch
        {
            ClientRunState.Applied or ClientRunState.AlreadyConfigured => RunMark.Done,
            ClientRunState.Failed => RunMark.Failed,
            ClientRunState.Configuring => RunMark.Warned,
            _ => RunMark.None,
        },
        outcome.Name,
        outcome.State switch
        {
            ClientRunState.Applied => InstallerCulture.Pick("settings applied", "настройки применены"),
            ClientRunState.AlreadyConfigured => InstallerCulture.Pick(
                "already configured; nothing changed", "уже настроен; ничего не изменено"),
            ClientRunState.Failed => InstallerCulture.Pick(
                "Configuration failed. Check this client's configuration and retry setup. Technical details: ",
                "Не удалось настроить. Проверьте конфигурацию этого клиента и повторите настройку. Технические сведения: ") + outcome.Reason,
            ClientRunState.Configuring => InstallerCulture.Pick(
                "Setup was interrupted; the final state is unknown. Check the client configuration before retrying.",
                "Настройка прервана; итоговое состояние неизвестно. Проверьте конфигурацию клиента перед повторной настройкой."),
            // Marked None: the package row already carries the failure.
            ClientRunState.Skipped => InstallerCulture.Pick(
                "skipped: this run did not install the Local AI package",
                "пропущено: этот запуск не установил пакет Local AI"),
            _ => InstallerCulture.Pick(
                "not attempted; the run stopped before client setup",
                "не выполнялось; запуск остановился до настройки клиента"),
        });

    public static RunResultRow Registration(RegistrationOutcome outcome) => new(
        outcome == RegistrationOutcome.Registered ? RunMark.Done : RunMark.Warned,
        InstallerCulture.Pick("Windows app registration", "Регистрация приложения в Windows"),
        outcome switch
        {
            RegistrationOutcome.Registered => InstallerCulture.Pick(
                "the uninstall entry was read back successfully", "запись удаления проверена чтением"),
            RegistrationOutcome.Missing => InstallerCulture.Pick(
                "the written uninstall entry was not found; run this installer again and choose Remove when needed",
                "записанная запись удаления не найдена; для удаления снова запустите установщик и выберите удаление"),
            _ => InstallerCulture.Pick(
                "could not register removal in Windows; run this installer again and choose Remove when needed",
                "не удалось зарегистрировать удаление в Windows; для удаления снова запустите установщик и выберите удаление"),
        });

    public static string PackageDetail(PackageOutcome outcome, bool installed)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            PackageOutcomeKind.NoRelease => InstallerCulture.Pick(
                "not installed, no verified release was selected",
                "не установлен, проверенный релиз не выбран"),
            // The advice for a layout refusal is always in the log, and for a root without
            // versions it is to delete the directory.
            PackageOutcomeKind.Refused => string.Format(
                InstallerCulture.Pick(
                    "not installed: the installation directory failed safety checks. The log below says what to do with it. Technical details: {0}",
                    "не установлен: каталог установки не прошёл проверку безопасности. Что с ним делать, сказано в журнале ниже. Технические сведения: {0}"),
                outcome.Reason),
            PackageOutcomeKind.Failed when outcome.Failure is { } failure => FailureDetail(failure, outcome.Reason),
            PackageOutcomeKind.Failed => string.Format(
                InstallerCulture.Pick(
                    "not installed: the installation stopped with an error. Technical details: {0}",
                    "не установлен: установка остановилась с ошибкой. Технические сведения: {0}"),
                outcome.Reason),
            PackageOutcomeKind.Cancelled when outcome.StillInstalled is { } still => string.Format(
                InstallerCulture.Pick(
                    "not installed: the run was cancelled; {0} is still installed",
                    "не установлен: запуск отменён; по-прежнему установлен {0}"),
                still),
            PackageOutcomeKind.Cancelled => InstallerCulture.Pick(
                "not installed: the run was cancelled",
                "не установлен: запуск отменён"),
            _ when installed && outcome.Restored is { Count: > 0 } restored => string.Format(
                InstallerCulture.Pick(
                    "{0}, version {1}; put back from the release: {2}",
                    "{0}, версия {1}; из релиза возвращены: {2}"),
                PackageStatusName(outcome),
                outcome.Version,
                string.Join(", ", restored)),
            _ when installed => string.Format(
                InstallerCulture.Pick("{0}, version {1}", "{0}, версия {1}"),
                PackageStatusName(outcome),
                outcome.Version),
            // A failed activation leaves in place what the repair put back before it.
            _ when outcome.Restored is { Count: > 0 } kept => string.Format(
                InstallerCulture.Pick(
                    "{0}; put back from the release and kept: {1}",
                    "{0}; из релиза возвращены и оставлены: {1}"),
                NotInstalled(outcome).TrimEnd('.'),
                string.Join(", ", kept)),
            _ => NotInstalled(outcome),
        };

        static string NotInstalled(PackageOutcome outcome) =>
            $"{PackageStatusName(outcome)} — {outcome.Reason}".TrimEnd(' ', '—');
    }

    /// <summary>
    /// A size, a hash and another release are one row: the reader does the same about each, and a
    /// file from another release cannot be told from a changed one. The numbers are in the log.
    /// </summary>
    private static string FailureDetail(PackageFailureKind failure, string? detail) => failure switch
    {
        PackageFailureKind.FolderCopyDoesNotMatch => InstallerCulture.Pick(
            "not installed: localai-package.zip in the folder does not match the release manifest beside it — it is incomplete, altered or from another release. Copy all three files again from one release",
            "не установлен: localai-package.zip в папке не соответствует манифесту релиза рядом с ним — файл неполный, изменён или из другого релиза. Скопируйте заново все три файла одного релиза"),
        PackageFailureKind.DownloadDoesNotMatch => InstallerCulture.Pick(
            "not installed: the downloaded package does not match the signed release manifest, so it was not used. Run the installer again to download it anew",
            "не установлен: скачанный пакет не соответствует подписанному манифесту релиза, поэтому не использован. Запустите установщик снова, чтобы скачать его заново"),
        PackageFailureKind.ContentsRejected => InstallerCulture.Pick(
            "not installed: the package matches its release manifest, but its contents failed the installer's safety checks",
            "не установлен: пакет соответствует манифесту релиза, но его содержимое не прошло проверку безопасности установщика"),
        PackageFailureKind.NotInFolder => InstallerCulture.Pick(
            "not installed: localai-package.zip is not in the folder",
            "не установлен: в папке нет localai-package.zip"),
        PackageFailureKind.NoReadAccess => InstallerCulture.Pick(
            "not installed: this account cannot read localai-package.zip in the folder",
            "не установлен: у этой учётной записи нет доступа на чтение к localai-package.zip в папке"),
        PackageFailureKind.Unreadable => string.Format(
            InstallerCulture.Pick(
                "not installed: localai-package.zip in the folder could not be read. Technical details: {0}",
                "не установлен: не удалось прочитать localai-package.zip в папке. Технические сведения: {0}"),
            detail),
        PackageFailureKind.TooLarge => InstallerCulture.Pick(
            "not installed: localai-package.zip in the folder is larger than any release publishes",
            "не установлен: localai-package.zip в папке больше, чем бывает в релизе"),
        PackageFailureKind.NotDownloaded => string.Format(
            InstallerCulture.Pick(
                "not installed: the package could not be downloaded from GitHub. Technical details: {0}",
                "не установлен: не удалось скачать пакет с GitHub. Технические сведения: {0}"),
            detail),
        _ => string.Format(
            InstallerCulture.Pick(
                "not installed: the package could not be written to the installer's working folder. Technical details: {0}",
                "не установлен: не удалось записать пакет в рабочую папку установщика. Технические сведения: {0}"),
            detail),
    };

    public static string PathDetail(PathOutcome outcome) => outcome switch
    {
        PathOutcome.Added => InstallerCulture.Pick(
            "added for this account",
            "добавлен для этой учётной записи"),
        PathOutcome.AlreadyThere => InstallerCulture.Pick("already there", "уже был прописан"),
        _ => InstallerCulture.Pick(
            "could not be updated; use the full path to localai-launcher.exe until PATH is corrected",
            "не удалось изменить; до исправления PATH используйте полный путь к localai-launcher.exe"),
    };

    /// <summary>
    /// What the package installer did, in words rather than in the name of its enum member.
    /// The summary is read by a person; "ImmutableConflict" tells them nothing about what to
    /// do next, and on a Russian run it also arrives in the wrong language.
    /// </summary>
    private static string PackageStatusName(PackageOutcome outcome) => outcome.Status switch
    {
        LocalAiPackageInstallStatus.Installed =>
            InstallerCulture.Pick("installed", "установлен"),
        LocalAiPackageInstallStatus.AlreadyInstalled =>
            InstallerCulture.Pick("already installed", "уже был установлен"),
        LocalAiPackageInstallStatus.Refused =>
            InstallerCulture.Pick("refused", "отказано"),
        // A repair of the active version, refused: it stays installed and selected.
        LocalAiPackageInstallStatus.ImmutableConflict when outcome.PriorVersion is { } prior &&
            string.Equals(outcome.Version, prior, StringComparison.Ordinal) => InstallerCulture.Pick(
            "left as it was: its version folder holds files or folders that are not part of " +
            "the release, so it was not repaired",
            "оставлен как был: в каталоге этой версии есть файлы или папки не из релиза, " +
            "поэтому он не восстановлен"),
        LocalAiPackageInstallStatus.ImmutableConflict => InstallerCulture.Pick(
            "not installed: that version directory already holds different files",
            "не установлен: в каталоге этой версии уже лежат другие файлы"),
        LocalAiPackageInstallStatus.RolledBack =>
            InstallerCulture.Pick("rolled back", "откачено"),
        LocalAiPackageInstallStatus.RollbackFailed =>
            InstallerCulture.Pick("rollback failed", "откат не удался"),
        LocalAiPackageInstallStatus.ManualRecoveryRequired => InstallerCulture.Pick(
            "manual recovery required",
            "требуется ручное восстановление"),
        LocalAiPackageInstallStatus.Indeterminate => InstallerCulture.Pick(
            "state could not be determined",
            "состояние определить не удалось"),
        LocalAiPackageInstallStatus.Busy => InstallerCulture.Pick(
            "not installed: another Local AI installation or activation was under way",
            "не установлен: шла другая установка или активация Local AI"),
        _ => outcome.Status.ToString(),
    };

    /// <summary>
    /// What the Apps &amp; features entry turned out to be, read back rather than recalled.
    ///
    /// The line was written from the values the installer had just handed the registry, so it
    /// agreed with itself whatever the registry did — and a machine that finished an install
    /// saying «listed as "Local AI Toolkit" 0.2.1-RC16» had no such entry. Reading it back cannot
    /// explain why that happens, but it stops the log asserting it.
    /// </summary>
    public static string AppsAndFeatures(UninstallEntry? readBack, string uninstallerPath) =>
        readBack is null
            ? "Apps & features: the entry was written and is not there when read back. Local AI " +
                "package installation completed; model and client results are reported separately. To remove it later, run this installer again and " +
                "choose Remove."
            : $"Apps & features: listed as \"{readBack.DisplayName}\" " +
                $"{readBack.DisplayVersion}. Removal runs {uninstallerPath}.";

    /// <summary>
    /// The prerequisite tally, for the log.
    ///
    /// English, because the log is written beside an English journal — it went on picking the
    /// window's language after the rows took its place on the page, and a Russian machine ended
    /// its otherwise-English log in six Russian lines. And counters only: the package had a line
    /// of its own in English where it was installed, and this repeated it in the other language.
    /// </summary>
    public static string Tally(int requested, int installed, int skipped, int failed) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Prerequisites: {0} requested, {1} installed, {2} skipped, {3} failed.",
            requested,
            installed,
            skipped,
            failed);

    /// <summary>Whether a failed or cancelled run left anything to take back, for the log.</summary>
    public static string RollbackAvailability(bool reversible, string journalPath) =>
        reversible
            ? "Some of the applied actions are reversible and can be rolled back from the " +
                "finish page; the journal at " + journalPath + " records exactly what was done."
            : "Nothing this run applied is reversible by the installer. The journal " +
                "at " + journalPath + " records exactly what was done.";

    /// <summary>
    /// The one question this page exists to answer, and it was a log line under eighty others in
    /// a box that did not scroll. A run that configured no client has nothing to say here, and
    /// says nothing rather than offering a heading over an empty line.
    /// </summary>
    public static string? NextSteps(IReadOnlyList<string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        return configured.Count == 0
            ? null
            : string.Format(
                CultureInfo.InvariantCulture,
                PageLabels.RestartClients,
                string.Join(", ", configured));
    }
}
