using System.Text;
using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Removal;

/// <summary>One path an uninstall would delete, and the matrix row that put it there.</summary>
public sealed record RemovalPathEntry(RemovalItem Item, string Path, bool IsDirectory);

/// <summary>
/// What removing the hooks from one connected repository would do — or why it would not.
///
/// A repository that has moved or been deleted since it was connected is not a failure and
/// not something to retry: it is named, skipped, and reported, because the alternative is an
/// uninstall that stops halfway through somebody else's machine.
/// </summary>
public sealed record HookRemovalEntry(
    string RepositoryId,
    string CommonDirectory,
    string? HooksDirectory,
    IReadOnlyList<string> Dispatchers,
    IReadOnlyList<string> RestoredHooks,
    IReadOnlyList<string> ExcludePatterns,
    string? ExcludePath,
    string? SkipReason)
{
    public bool IsSkipped => SkipReason is not null;

    public bool HasWork =>
        !IsSkipped && (Dispatchers.Count > 0 || ExcludePatterns.Count > 0);
}

/// <summary>
/// Something an uninstall deliberately does not touch, said out loud rather than left for the
/// person to discover. Prerequisites and indexers may be used by other software; models may
/// serve other tools; the installer's own journal is the record of what installs did.
/// </summary>
public sealed record RetainedNotice(string Title, string Detail);

/// <summary>
/// Everything one uninstall would do, before any of it has been done.
///
/// Same shape as an installation: the whole effect is listed, nothing runs before it has been
/// confirmed, and what the plan names is exactly what apply performs. Client configurations
/// ride along as ordinary <see cref="AgentConfigurationPlan"/>s, so removal inherits the
/// backups, the read-back and the refusal on a concurrent edit that installation already has.
///
/// <see cref="LeavesDispatchersWithoutLauncher"/>: the launcher goes and a connected
/// repository keeps a dispatcher that calls it.
/// </summary>
public sealed record UninstallPlan(
    string RuntimeRoot,
    RemovalSelection Selection,
    IReadOnlyList<RemovalPathEntry> Paths,
    IReadOnlyList<string> RetainedPaths,
    IReadOnlyList<AgentConfigurationPlan> AgentConfigurations,
    IReadOnlyList<HookRemovalEntry> Hooks,
    IReadOnlyList<RetainedNotice> Retained,
    bool RemovesAppsAndFeaturesEntry = false,
    bool RemovesLauncherFromPath = false,
    bool LeavesDispatchersWithoutLauncher = false)
{
    public bool HasWork =>
        Paths.Count > 0 ||
        AgentConfigurations.Any(plan => plan.HasChanges) ||
        Hooks.Any(hook => hook.HasWork) ||
        RemovesAppsAndFeaturesEntry ||
        RemovesLauncherFromPath;

    /// <summary>
    /// Whether the runtime root would be left with nothing in it.
    ///
    /// Read from what is on disk, not from which boxes are ticked: a full uninstall that keeps
    /// the signing keys — the default — still leaves the root standing, and a finish page that
    /// said otherwise would be wrong on the most common run there is.
    /// </summary>
    public bool RemovesRuntimeRootEntirely => RetainedPaths.Count == 0;

    /// <summary>
    /// The review page's text: every removal named, every skip named, every retention named.
    /// A path that is not in here is a path apply must not touch.
    /// </summary>
    public string PreviewText
    {
        get
        {
            var (changes, retained) = PreviewSections();
            return changes + retained;
        }
    }

    /// <summary>One projection supplies both the grouped overview and the technical preview.</summary>
    public (string Changes, string Retained) PreviewSections()
    {
        var text = new StringBuilder();
        var keptHooks = new StringBuilder();
        foreach (var item in RemovalMatrix.Items)
        {
            var paths = Paths.Where(path => path.Item == item).ToArray();
            var agents = AgentPlansFor(item);
            var hooks = item == RemovalItem.GitHooks ? Hooks : [];
            var registration = item == RemovalItem.Binaries && RemovesAppsAndFeaturesEntry;
            var launcherPath = item == RemovalItem.Binaries && RemovesLauncherFromPath;
            if (paths.Length == 0 && agents.Length == 0 && hooks.Count == 0 &&
                !registration && !launcherPath)
            {
                continue;
            }

            text.AppendLine(RemovalMatrix.Title(item) + ":");
            foreach (var path in paths)
            {
                text.AppendLine(
                    "  " + InstallerCulture.Pick("remove", "удалить") + " " +
                    path.Path + (path.IsDirectory ? "\\" : string.Empty));
            }

            if (item == RemovalItem.Binaries && RemovesLauncherFromPath)
            {
                text.AppendLine(InstallerCulture.Pick(
                    "  take the launcher off this account's PATH",
                    "  убрать launcher из PATH этой учётной записи"));
            }

            if (registration)
            {
                // The entry is what made this removable from Apps & features in the first
                // place, so it is named here. It goes first and the copy it runs right after,
                // before any other path, so a run stopped halfway leaves no entry whose program
                // is gone.
                text.AppendLine(InstallerCulture.Pick(
                    "  remove the Apps & features entry, then the uninstaller copy it runs",
                    "  удалить запись в «Приложениях и возможностях», затем копию " +
                    "деинсталлятора, которую она запускает"));
            }

            foreach (var file in agents.SelectMany(plan => plan.Files))
            {
                // A deletion said as a deletion. The skill file is removed rather than
                // edited, and calling that "rewrite" understates the only line on this page
                // that takes a whole file out of somebody's home directory.
                text.AppendLine(
                    "  " + (file.Deletes
                        ? InstallerCulture.Pick("remove", "удалить")
                        : InstallerCulture.Pick("rewrite", "переписать")) +
                    " " + file.Path);
                AppendBackup(text, file);
            }

            foreach (var hook in hooks)
            {
                if (!hook.HasWork)
                {
                    keptHooks.AppendLine(HookLine(hook));
                    continue;
                }
                text.AppendLine(HookLine(hook));
                foreach (var dispatcher in hook.Dispatchers)
                {
                    text.AppendLine(
                        "    " + InstallerCulture.Pick("remove", "удалить") + " " + dispatcher);
                }

                foreach (var restored in hook.RestoredHooks)
                {
                    text.AppendLine(
                        "    " + InstallerCulture.Pick("restore", "восстановить") + " " + restored);
                }

                if (hook.ExcludePatterns.Count > 0 && hook.ExcludePath is not null)
                {
                    text.AppendLine(
                        "    " + InstallerCulture.Pick("clean", "очистить") + " " +
                        hook.ExcludePath + ": " +
                        string.Join(", ", hook.ExcludePatterns));
                }
            }
        }

        // The gate, which the loop above cannot reach. AgentPlansFor looks plans up by
        // the client's name, and this one is deliberately named apart from it so that a
        // stray comma in settings.json cannot take the MCP registration down with it — so
        // the file it rewrites was named in no preview, on any preset, while the finish
        // report named it afterwards. The page's whole contract is that what it does not
        // name, the run does not touch.
        //
        // Its own heading rather than the client's: on a run where the Claude row is
        // unticked — which is what the reinstall-friendly preset does, and where the gate
        // still goes with the binaries — a "Claude integration:" heading would be a second
        // untruth in place of the first.
        if (AgentConfigurations.FirstOrDefault(plan =>
                string.Equals(
                    plan.AgentName,
                    ClaudeConfigurationAdapter.HooksName,
                    StringComparison.Ordinal) &&
                plan.HasChanges) is { } settingsPlan)
        {
            text.AppendLine(InstallerCulture.Pick(
                "Claude Code hooks:",
                "Хуки Claude Code:"));
            foreach (var file in settingsPlan.Files)
            {
                // A line per effect rather than per file. Both effects live in one document,
                // so a per-file loop would print one line for two removals — and a heading
                // promising two while one happens is the kind of lie this page exists to
                // prevent. "remove … from", not the loop's bare "rewrite": the file stays,
                // and a removal that reads as a deletion is the one thing this must not say
                // about somebody's own settings.
                if (Leaves(file, ClaudeHook.Gate))
                {
                    text.AppendLine(
                        "  " + InstallerCulture.Pick(
                            "remove the text-search refusal from ",
                            "убрать отклонение текстового поиска из ") + file.Path);
                }

                if (Leaves(file, ClaudeHook.WorktreeCreate) ||
                    Leaves(file, ClaudeHook.WorktreeRemove))
                {
                    text.AppendLine(
                        "  " + InstallerCulture.Pick(
                            "remove worktree placement from ",
                            "убрать размещение worktree из ") + file.Path);
                }

                AppendBackup(text, file);
            }
        }

        if (!HasWork)
        {
            text.AppendLine(RemovalMatrix.Items.Any(Selection.Includes)
                ? InstallerCulture.Pick(
                    "None of the selected items was found: this run would change nothing.",
                    "Ничего из выбранного не найдено: этот запуск ничего не изменит.")
                : InstallerCulture.Pick(
                    "Nothing selected: this run would change nothing.",
                    "Ничего не выбрано: этот запуск ничего не изменит."));
        }

        var changes = text.ToString();
        text.Clear();
        text.Append(keptHooks);

        if (RetainedPaths.Count > 0)
        {
            text.AppendLine(
                InstallerCulture.Pick("Left in ", "Остаётся в ") + RuntimeRoot + ":");
            foreach (var path in RetainedPaths)
            {
                text.AppendLine(
                    "  " + InstallerCulture.Pick("keep", "оставить") + " " + path);
            }
        }

        foreach (var notice in Retained)
        {
            text.AppendLine(
                InstallerCulture.Pick("Kept: ", "Оставлено: ") +
                notice.Title + " — " + notice.Detail);
        }

        return (changes, text.ToString());
    }

    /// <summary>
    /// Whether this file plan takes one hook out: ours is in the document now and not in the one
    /// the run would write.
    ///
    /// Asked of the planned text rather than of a flag, so the page cannot claim a removal the
    /// splice did not make. A document neither side can parse answers no — the plan that would
    /// rewrite it fails loudly on its own, and a preview line is not the place to report that.
    /// </summary>
    private static bool Leaves(AgentConfigurationFilePlan file, ClaudeHook hook)
    {
        try
        {
            return ClaudeHookRegistration.IsRegistered(file.BeforeText, hook) &&
                !ClaudeHookRegistration.IsRegistered(file.AfterText, hook);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The apply parks what it displaces at the backup path, a deleted file included, and leaves
    /// it there. Removal plans only files that have content, so every one of them gets one.
    /// </summary>
    private static void AppendBackup(StringBuilder text, AgentConfigurationFilePlan file) =>
        text.AppendLine(
            "    " + InstallerCulture.Pick(
                "keep a copy of the previous version: ",
                "оставить копию прежней версии: ") + file.BackupPath);

    private AgentConfigurationPlan[] AgentPlansFor(RemovalItem item)
    {
        var agent = item switch
        {
            RemovalItem.ClaudeIntegration => "Claude",
            RemovalItem.CodexIntegration => "Codex",
            _ => null,
        };
        return agent is null
            ? []
            : AgentConfigurations
                .Where(plan =>
                    string.Equals(plan.AgentName, agent, StringComparison.Ordinal) &&
                    plan.HasChanges)
                .ToArray();
    }

    private static string HookLine(HookRemovalEntry hook) =>
        hook.IsSkipped
            ? "  " + InstallerCulture.Pick("skip", "пропустить") + " " +
                hook.CommonDirectory + " — " + hook.SkipReason
            : "  " + hook.CommonDirectory +
                (hook.HasWork
                    ? string.Empty
                    : InstallerCulture.Pick(
                        " — no managed dispatchers found",
                        " — управляемых диспетчеров не найдено"));
}
