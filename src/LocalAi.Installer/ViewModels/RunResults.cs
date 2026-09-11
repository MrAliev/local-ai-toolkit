using System.Globalization;
using LocalAi.Installer.Core;

namespace LocalAi.Installer.ViewModels;

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
        var ready = models.Count(model => model.State == ModelRunState.Ready);
        var mark = models.Count == 0
            ? RunMark.None
            : models.Any(model => model.State is ModelRunState.Failed or ModelRunState.NotReached)
                ? RunMark.Failed
                : models.Any(model => model.State == ModelRunState.Rejected)
                    ? RunMark.Warned
                    : RunMark.Done;
        return new(
            mark,
            PageLabels.LocalModels,
            models.Count == 0
                ? PageLabels.ResultNotAttempted
                : string.Format(
                    CultureInfo.InvariantCulture,
                    PageLabels.ResultModelsReady,
                    ready,
                    models.Count));
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
