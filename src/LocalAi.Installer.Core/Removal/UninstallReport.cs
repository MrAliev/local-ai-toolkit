using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Removal;

public enum RemovalReportState { Completed, Failed, Unknown, NotStarted, Retained }
public enum RemovalReportKind { Path, Configuration, Hooks, LauncherPath, Registration, ProcessStop, Other }

public sealed record RemovalReportEntry(
    string Target, RemovalReportKind Kind, RemovalReportState State, string? Detail = null);

/// <summary>Shared journal identities; presentation never parses paths out of prose.</summary>
public static class UninstallJournalDescriptions
{
    public static string Path(string path) => "Remove " + path;
    public static string Configuration(AgentConfigurationPlan configuration) =>
        "Disconnect " + configuration.AgentName + ": " + string.Join(", ", configuration.Files.Select(file => file.Path));
    public static string Hooks(string commonDirectory) => "Remove the LocalAi hooks from " + commonDirectory;
    public static string LauncherPath(string directory) => "Take " + directory + " off the user PATH";
    public static string Registration(string directory) => "Remove the Apps & features entry and " + directory;
}

public static class UninstallReport
{
    /// <summary>
    /// The plan supplies only the inventory. Completion comes exclusively from the journal.
    /// A running intent has an unknown outcome; a failure may include partial effects.
    /// A journal step the plan does not name goes before the first planned one that did not
    /// start ahead of it, so the stop, which runs first, is reported first.
    /// </summary>
    public static IReadOnlyList<RemovalReportEntry> Capture(
        UninstallPlan plan, InstallerRunJournalSnapshot? journal, InstallationLayout layout)
    {
        var remaining = journal?.Steps.ToList() ?? [];
        var entries = new List<(RemovalReportEntry Entry, bool Planned, DateTimeOffset? Started)>();
        foreach (var configuration in plan.AgentConfigurations.Where(item => item.HasChanges))
        {
            Add(string.Join(", ", configuration.Files.Select(file => file.Path)),
                RemovalReportKind.Configuration, UninstallJournalDescriptions.Configuration(configuration));
            // Read from disk: a step that failed part-way rolled back from the backups it had
            // made, and those stay, while the files it never reached have none.
            entries.AddRange(configuration.Files
                .Where(file => File.Exists(file.BackupPath))
                .Select(file => Unplanned(new RemovalReportEntry(
                    file.BackupPath,
                    RemovalReportKind.Configuration,
                    RemovalReportState.Retained,
                    InstallerCulture.Pick("the previous version of ", "прежняя версия ") + file.Path))));
        }

        foreach (var hook in plan.Hooks)
        {
            if (hook.HasWork)
            {
                Add(hook.CommonDirectory, RemovalReportKind.Hooks, UninstallJournalDescriptions.Hooks(hook.CommonDirectory));
            }
            else if (hook.SkipReason is { } reason)
            {
                entries.Add(Unplanned(new(hook.CommonDirectory, RemovalReportKind.Hooks, RemovalReportState.Retained, reason)));
            }
        }

        foreach (var path in plan.Paths)
        {
            Add(path.Path, RemovalReportKind.Path, UninstallJournalDescriptions.Path(path.Path));
        }

        if (plan.RemovesLauncherFromPath)
        {
            var directory = System.IO.Path.Combine(layout.BinRoot, "launcher");
            Add(directory, RemovalReportKind.LauncherPath, UninstallJournalDescriptions.LauncherPath(directory));
        }

        if (plan.RemovesAppsAndFeaturesEntry && OperatingSystem.IsWindows())
        {
            var directory = new UninstallRegistration(layout).UninstallerDirectory;
            Add(directory, RemovalReportKind.Registration, UninstallJournalDescriptions.Registration(directory));
        }

        foreach (var step in remaining)
        {
            var before = entries.FindIndex(item => item.Planned && !(item.Started < step.StartedAtUtc));
            entries.Insert(
                before < 0 ? entries.Count : before,
                Unplanned(new(
                    step.Description,
                    step.Kind == InstallerRunEffectKind.ProcessStop ? RemovalReportKind.ProcessStop : RemovalReportKind.Other,
                    State(step.Status),
                    step.Detail)));
        }

        entries.AddRange(plan.RetainedPaths.Select(path =>
            Unplanned(new RemovalReportEntry(path, RemovalReportKind.Path, RemovalReportState.Retained))));
        entries.AddRange(plan.Retained.Select(notice =>
            Unplanned(new RemovalReportEntry(notice.Title, RemovalReportKind.Other, RemovalReportState.Retained, notice.Detail))));
        return entries.Select(item => item.Entry).ToArray();

        void Add(string target, RemovalReportKind kind, string description)
        {
            var step = remaining.FirstOrDefault(item => item.Description == description);
            if (step is not null)
            {
                remaining.Remove(step);
            }

            entries.Add((
                new(target, kind, step is null ? RemovalReportState.NotStarted : State(step.Status), step?.Detail),
                true,
                step?.StartedAtUtc));
        }

        static (RemovalReportEntry, bool, DateTimeOffset?) Unplanned(RemovalReportEntry entry) => (entry, false, null);
    }

    private static RemovalReportState State(InstallerRunStepStatus status) => status switch
    {
        InstallerRunStepStatus.Completed => RemovalReportState.Completed,
        InstallerRunStepStatus.Failed => RemovalReportState.Failed,
        _ => RemovalReportState.Unknown,
    };
}
