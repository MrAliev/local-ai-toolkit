using System.Collections.ObjectModel;
using LocalAi.Installer.Core;
namespace LocalAi.Installer.ViewModels;

public sealed class FinishPageViewModel : ObservableObject
{
    private bool success;
    private bool requiresRestart;
    private string? runLog;
    private string? progress;
    private string? rollbackReport;
    private string? nextSteps;
    private string? reportPath;

    /// <summary>
    /// One row per thing the run touched. The page used to open with four counters that read
    /// "0. 0. 0. 0." whenever nobody had ticked a prerequisite, which is the ordinary run.
    /// </summary>
    public ObservableCollection<RunResultRow> Results { get; } = [];

    /// <summary>
    /// The only question somebody has on this page. It was a log line under eighty others, in a
    /// box that did not scroll to it.
    /// </summary>
    public string? NextSteps
    {
        get => nextSteps;
        set
        {
            SetProperty(ref nextSteps, value);
            OnPropertyChanged(nameof(HasNextSteps));
        }
    }

    public bool HasNextSteps => !string.IsNullOrWhiteSpace(nextSteps);

    /// <summary>
    /// Its own field, not a sentence glued onto the summary: as part of the summary it was the
    /// last line of a box 110 high and got clipped mid-path.
    /// </summary>
    public string? ReportPath
    {
        get => reportPath;
        set
        {
            SetProperty(ref reportPath, value);
            OnPropertyChanged(nameof(HasReportPath));
        }
    }

    public bool HasReportPath => !string.IsNullOrWhiteSpace(reportPath);

    public bool Success
    {
        get => success;
        set
        {
            SetProperty(ref success, value);
        }
    }

    public bool RequiresRestart
    {
        get => requiresRestart;
        set
        {
            SetProperty(ref requiresRestart, value);
            OnPropertyChanged(nameof(RestartNotice));
        }
    }

    /// <summary>
    /// What the run did, line by line. It was called RollbackNotes, which promised an
    /// installation that could be undone; there has never been one, and the value has
    /// always been the log.
    /// </summary>
    public string? RunLog
    {
        get => runLog;
        set
        {
            SetProperty(ref runLog, value);
        }
    }

    public string? Progress
    {
        get => progress;
        set
        {
            SetProperty(ref progress, value);
        }
    }

    /// <summary>
    /// What rollback actually did, effect by effect: undone, left in place, or failed.
    /// Distinct from <see cref="RunLog"/> on purpose — the log is what the run did, this is
    /// what was taken back, and collapsing the two is how "RollbackNotes" came to mean
    /// nothing.
    /// </summary>
    public string? RollbackReport
    {
        get => rollbackReport;
        set
        {
            SetProperty(ref rollbackReport, value);
            OnPropertyChanged(nameof(HasRollbackReport));
        }
    }

    public bool HasRollbackReport => !string.IsNullOrWhiteSpace(rollbackReport);

    public string RestartNotice => RequiresRestart
        ? InstallerCulture.Pick("Restart required.", "Требуется перезагрузка.")
        : InstallerCulture.Pick("No restart needed.", "Перезагрузка не нужна.");
}
