using System.Globalization;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Activation;
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
}

/// <summary>What the package step ended in, kept as facts so its row can be worded in any language.</summary>
public sealed record PackageOutcome(
    PackageOutcomeKind Kind,
    LocalAiPackageInstallStatus Status = default,
    string? Version = null,
    string? Reason = null);

public enum PathOutcome
{
    Added,
    AlreadyThere,
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

    public static RunResultRow Prerequisites(int requested, int installed, int failed) =>
        new(
            requested == 0 ? RunMark.None : failed > 0 ? RunMark.Failed : RunMark.Done,
            PageLabels.ResultPrerequisites,
            requested == 0
                ? PageLabels.ResultNothingSelected
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
    /// already on the install page, row by row.
    /// </summary>
    public static RunResultRow Models(IReadOnlyList<ModelRunRow> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var limited = models.Count(model => model.State == ModelRunState.ReadyWithLimitations);
        var ready = models.Count(model => model.State == ModelRunState.Ready) + limited;
        var mark = models.Count == 0
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
            models.Count == 0
                ? PageLabels.ResultNotAttempted
                : string.Join("; ", details));

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

    public static RunResultRow PathEntry(RunMark mark, string detail) =>
        new(mark, PageLabels.ResultPath, detail);

    public static string PackageDetail(PackageOutcome outcome, bool installed)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            PackageOutcomeKind.NoRelease => InstallerCulture.Pick(
                "not installed, no verified release was selected",
                "не установлен, проверенный релиз не выбран"),
            PackageOutcomeKind.Refused => string.Format(
                InstallerCulture.Pick("not installed — {0}", "не установлен — {0}"),
                outcome.Reason),
            _ when installed => string.Format(
                InstallerCulture.Pick("{0}, version {1}", "{0}, версия {1}"),
                PackageStatusName(outcome.Status),
                outcome.Version),
            _ => $"{PackageStatusName(outcome.Status)} — {outcome.Reason}".TrimEnd(' ', '—'),
        };
    }

    public static string PathDetail(PathOutcome outcome) => outcome switch
    {
        PathOutcome.Added => InstallerCulture.Pick(
            "added for this account",
            "добавлен для этой учётной записи"),
        PathOutcome.AlreadyThere => InstallerCulture.Pick("already there", "уже был прописан"),
        _ => InstallerCulture.Pick("could not be updated", "не удалось изменить"),
    };

    /// <summary>
    /// What the package installer did, in words rather than in the name of its enum member.
    /// The summary is read by a person; "ImmutableConflict" tells them nothing about what to
    /// do next, and on a Russian run it also arrives in the wrong language.
    /// </summary>
    private static string PackageStatusName(LocalAiPackageInstallStatus status) => status switch
    {
        LocalAiPackageInstallStatus.Installed =>
            InstallerCulture.Pick("installed", "установлен"),
        LocalAiPackageInstallStatus.AlreadyInstalled =>
            InstallerCulture.Pick("already installed", "уже был установлен"),
        LocalAiPackageInstallStatus.Refused =>
            InstallerCulture.Pick("refused", "отказано"),
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
            "another install or uninstall was running",
            "шла другая установка или деинсталляция"),
        _ => status.ToString(),
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
            ? "Apps & features: the entry was written and is not there when read back. LocalAi " +
                "is installed and works; to remove it later, run this installer again and " +
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
