using System.Text;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Repository;

namespace LocalAi.Installer.Core.Removal;

/// <summary>Something the run could not take away, and why. Named rather than swallowed.</summary>
public sealed record UninstallFailure(string Path, string Reason);

public sealed record UninstallOutcome(
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> RewrittenConfigurations,
    IReadOnlyList<string> RemovedHooks,
    IReadOnlyList<UninstallFailure> Failures,
    bool ProcessesStopped,
    bool RuntimeRootRemoved,
    bool AppsAndFeaturesEntryRemoved = false,

    /// <summary>
    /// The uninstaller could not delete its own running copy, so the operating system will do
    /// it at the next restart. Reported rather than hidden: a folder that outlives an
    /// uninstall by design is only reassuring if somebody said it would.
    /// </summary>
    bool UninstallerRemovalDeferred = false)
{
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Raised when the run stopped before touching anything, because the step that had to come
/// first did not happen.
/// </summary>
public sealed class UninstallRefusedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Performs exactly what an <see cref="UninstallPlan"/> said it would, in the one order that
/// is safe.
///
/// The broker is asked to finish first, through the launcher's own stop — the same machinery
/// an upgrade uses, which writes the shutdown request and waits for the process to go before
/// killing anything. That has to complete before the runtime root is touched: deleting the
/// files underneath a running broker leaves a half-removed tree and a process still holding
/// what is left of it. If the stop cannot be performed, the run refuses rather than starting.
///
/// Everything after that is best-effort and reported: a file another program is holding open
/// stops that one path, not the uninstall. Every effect is journalled intent-first, and none
/// of them is marked reversible — an uninstall is not a transaction to be rolled back, and a
/// journal that claimed otherwise would offer to restore a tree it no longer has.
/// </summary>
public sealed class UninstallRunner(
    InstallationLayout layout,
    IProcessRunner processRunner,
    TimeSpan? stopTimeout = null,
    Func<string, byte[]>? readBackOverride = null,
    string? registrySubKey = null,
    string? selfDirectory = null,
    Action<string>? removeUninstallerAfterExit = null)
{
    private readonly TimeSpan stopTimeout = stopTimeout ?? TimeSpan.FromMinutes(2);

    /// <summary>
    /// Where this executable is running from. It matters because the uninstaller is normally a
    /// copy parked inside the very tree it is deleting: Windows will not remove a running
    /// executable, so that one directory is left until last and then handed to the operating
    /// system. A test points this elsewhere to exercise both paths.
    /// </summary>
    private readonly string selfDirectory =
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            selfDirectory ?? AppContext.BaseDirectory));

    /// <summary>
    /// <paramref name="progress"/> is how the page in front of somebody learns that this is
    /// still happening. The removal used to report nothing between starting and finishing, and
    /// asking the broker to stop alone can take two minutes — a bar that has not moved for two
    /// minutes is what makes people kill installers.
    /// </summary>
    public async Task<UninstallOutcome> ApplyAsync(
        UninstallPlan plan,
        InstallerRunJournal journal,
        CancellationToken cancellationToken = default,
        IProgress<UninstallProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(journal);

        var stopped = await StopRunningToolsAsync(plan, journal, cancellationToken, progress);
        var failures = new List<UninstallFailure>();
        progress?.Report(new(25, InstallerCulture.Pick(
            "Updating client configurations…",
            "Обновляю конфигурации клиентов…")));
        var rewritten = await RewriteClientConfigurationsAsync(
            plan,
            journal,
            failures,
            cancellationToken);
        progress?.Report(new(45, InstallerCulture.Pick(
            "Removing Git hook dispatchers…",
            "Удаляю диспетчеры хуков Git…")));
        var hooks = RemoveHooks(plan, journal, failures, cancellationToken);
        progress?.Report(new(60, InstallerCulture.Pick("Removing files…", "Удаляю файлы…")));
        var removed = RemovePaths(plan, journal, failures, cancellationToken, progress);
        progress?.Report(new(90, InstallerCulture.Pick(
            "Finishing the removal…",
            "Завершаю удаление…")));
        cancellationToken.ThrowIfCancellationRequested();
        // Before the entry and the uninstaller's own copy, which is the last thing that happens:
        // the PATH entry names a directory the binaries have already left.
        RemoveLauncherPath(plan, journal, failures);
        var (unregistered, deferred) = RemoveRegistration(plan, journal, failures);
        RemoveDirectoriesLeftEmptyByTheSweep(plan, removed);
        return new UninstallOutcome(
            removed,
            rewritten,
            hooks,
            failures,
            stopped,
            RemoveEmptyRuntimeRoot(plan, failures),
            unregistered,
            deferred);
    }

    /// <summary>
    /// Takes the Apps &amp; features entry out and then removes the uninstaller's own copy —
    /// last of everything, because it is the file this process is running from. An entry that
    /// was already gone is not a failure; a copy that cannot be deleted while it runs is
    /// scheduled for the next restart and reported as such.
    /// </summary>
    /// <summary>
    /// Takes the launcher's directory off this account's PATH. An entry somebody removed by hand
    /// is not a failure, and a PATH that cannot be written is reported rather than thrown: the
    /// binaries are already gone by then, and an entry pointing at nothing is untidy rather than
    /// harmful.
    /// </summary>
    private void RemoveLauncherPath(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures)
    {
        if (!plan.RemovesLauncherFromPath || !OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(layout.BinRoot, "launcher");
        var step = journal.BeginStep(
            InstallerRunEffectKind.LauncherPath,
            UninstallJournalDescriptions.LauncherPath(directory));
        try
        {
            LauncherPathRegistration.Unregister(directory);
            journal.CompleteStep(step, "Removed from the user PATH.", isReversible: false);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or InvalidOperationException)
        {
            journal.FailStep(step, exception.Message);
            failures.Add(new UninstallFailure(directory, exception.Message));
        }
    }

    private (bool Unregistered, bool Deferred) RemoveRegistration(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures)
    {
        if (!plan.RemovesAppsAndFeaturesEntry || !OperatingSystem.IsWindows())
        {
            return (false, false);
        }

        var registration = new UninstallRegistration(
            layout,
            registrySubKey,
            removeUninstallerAfterExit);
        var step = journal.BeginStep(
            InstallerRunEffectKind.RuntimeRemoval,
            UninstallJournalDescriptions.Registration(registration.UninstallerDirectory));
        try
        {
            var unregistered = registration.Unregister();
            var removedNow = registration.RemoveUninstallerCopy();
            journal.CompleteStep(
                step,
                removedNow
                    ? "Removed."
                    : "Entry removed; the uninstaller's own copy goes at the next restart.",
                isReversible: false);
            return (unregistered, !removedNow);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            journal.FailStep(step, exception.Message);
            failures.Add(new UninstallFailure(
                registration.UninstallerDirectory,
                exception.Message));
            return (false, false);
        }
    }

    /// <summary>
    /// Asks the launcher to stop everything running out of this installation, and only then
    /// lets the rest of the run proceed.
    ///
    /// Skipped when nothing in the runtime root is being removed — disconnecting clients leaves
    /// the runtime alone, and stopping a broker somebody is using to answer a question would be
    /// a gratuitous interruption. Skipped too when the launcher is not there: there is nothing
    /// to ask, and refusing would block the very run that clears up a broken installation.
    ///
    /// For the same reason a pointer the launcher cannot resolve is not a refusal: it fails
    /// before looking at any process, so each version directory is asked by name instead.
    /// </summary>
    private async Task<bool> StopRunningToolsAsync(
        UninstallPlan plan,
        InstallerRunJournal journal,
        CancellationToken cancellationToken,
        IProgress<UninstallProgress>? progress)
    {
        if (plan.Paths.Count == 0 || !File.Exists(layout.LauncherPath))
        {
            return false;
        }

        progress?.Report(new(0, InstallerCulture.Pick(
            "Asking LocalAi to stop…",
            "Прошу LocalAi остановиться…")));
        var step = journal.BeginStep(
            InstallerRunEffectKind.ProcessStop,
            "Ask LocalAi to finish and exit: " + layout.LauncherPath + " stop");
        try
        {
            var result = await processRunner.RunAsync(
                layout.LauncherPath,
                ["stop"],
                stopTimeout,
                cancellationToken);
            if (result.ExitCode != 0 && IsUnresolvedInstallation(result))
            {
                result = await StopEachVersionAsync(cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                var detail = Detail(result);
                journal.FailStep(step, detail);
                // This sentence is the removal wizard's finish summary, word for word, so
                // it is prose rather than machine text. The detail after it is the launcher's.
                throw new UninstallRefusedException(
                    InstallerCulture.Pick(
                        "Stopping LocalAi failed, so nothing was removed. ",
                        "Остановить LocalAi не удалось, поэтому ничего не удалено. ") +
                    detail);
            }

            journal.CompleteStep(step, "Stopped.", isReversible: false);
            return true;
        }
        catch (Exception exception) when (exception is not (
            UninstallRefusedException or OperationCanceledException))
        {
            journal.FailStep(step, exception.Message);
            throw new UninstallRefusedException(
                InstallerCulture.Pick(
                    "LocalAi could not be asked to stop, so nothing was removed. ",
                    "LocalAi не удалось попросить остановиться, поэтому ничего не " +
                    "удалено. ") +
                exception.Message,
                exception);
        }
    }

    private static bool IsUnresolvedInstallation(ProcessResult result)
    {
        var error = result.StandardError.TrimStart();
        return error.StartsWith("current_pointer_invalid:", StringComparison.Ordinal) ||
            error.StartsWith("version_incomplete:", StringComparison.Ordinal);
    }

    /// <summary>
    /// `stop --version` for every directory under the versions root, stopping at the first that
    /// fails. No directory at all is nothing to stop.
    /// </summary>
    private async Task<ProcessResult> StopEachVersionAsync(CancellationToken cancellationToken)
    {
        var versions = Directory.Exists(layout.VersionsRoot)
            ? Directory.GetDirectories(layout.VersionsRoot)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        foreach (var version in versions)
        {
            var result = await processRunner.RunAsync(
                layout.LauncherPath,
                ["stop", "--version", Path.GetFileName(version)],
                stopTimeout,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                return result;
            }
        }

        return new ProcessResult(0, string.Empty, string.Empty, false, false);
    }

    private async Task<IReadOnlyList<string>> RewriteClientConfigurationsAsync(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures,
        CancellationToken cancellationToken)
    {
        var rewritten = new List<string>();
        foreach (var configuration in plan.AgentConfigurations.Where(plan => plan.HasChanges))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = journal.BeginStep(
                InstallerRunEffectKind.AgentConfiguration,
                UninstallJournalDescriptions.Configuration(configuration));
            try
            {
                await AgentConfigurationApply.ApplyAsync(
                    configuration,
                    cancellationToken,
                    readBackOverride);
                rewritten.AddRange(configuration.Files.Select(file => file.Path));
                journal.CompleteStep(step, "Disconnected.", isReversible: false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                journal.FailStep(step, exception.Message);
                foreach (var file in configuration.Files)
                {
                    failures.Add(new UninstallFailure(file.Path, exception.Message));
                }
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Takes the dispatchers out of each repository, puts back whatever they were chaining, and
    /// removes the ignore rules installation added for them. A repository the plan already
    /// marked as skipped is left exactly alone.
    /// </summary>
    private IReadOnlyList<string> RemoveHooks(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures,
        CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var hook in plan.Hooks.Where(hook => hook.HasWork))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = journal.BeginStep(
                InstallerRunEffectKind.GitHookRemoval,
                UninstallJournalDescriptions.Hooks(hook.CommonDirectory));
            var before = removed.Count;
            try
            {
                foreach (var dispatcher in hook.Dispatchers)
                {
                    File.Delete(dispatcher);
                    removed.Add(dispatcher);
                    var chained = dispatcher + GitHookLayout.ChainedSuffix;
                    if (File.Exists(chained))
                    {
                        // What was there before us goes back where it was: the dispatcher only
                        // ever borrowed the name.
                        File.Move(chained, dispatcher, overwrite: true);
                    }
                }

                CleanExclude(hook);
                journal.CompleteStep(
                    step,
                    removed.Count - before + " dispatcher(s) removed.",
                    isReversible: false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                journal.FailStep(step, exception.Message);
                failures.Add(new UninstallFailure(
                    hook.HooksDirectory ?? hook.CommonDirectory,
                    exception.Message));
            }
        }

        return removed;
    }

    private static void CleanExclude(HookRemovalEntry hook)
    {
        if (hook.ExcludePath is not { Length: > 0 } path || hook.ExcludePatterns.Count == 0)
        {
            return;
        }

        var ours = new HashSet<string>(hook.ExcludePatterns, StringComparer.Ordinal);
        var existing = File.ReadAllText(path);
        var newLine = GitHookLayout.LineEndingOf(existing);
        var kept = existing
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n')
            .Where(line => !ours.Contains(line))
            .ToList();

        // Exactly one, which is what the install added: it separates our block from whatever came
        // before it, and only when the file did not already end in a blank line. Stripping every
        // trailing blank took the reader's own as well, and the comment here said "the blank
        // line" while the loop said all of them.
        if (kept.Count > 0 && kept[^1].Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        // The file is Git's and is left ending in exactly one newline, as Git writes it — in the
        // endings it arrived with, so removing our two patterns does not reflow the rest.
        File.WriteAllText(
            path,
            kept.Count == 0 ? string.Empty : string.Join(newLine, kept) + newLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private IReadOnlyList<string> RemovePaths(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures,
        CancellationToken cancellationToken,
        IProgress<UninstallProgress>? progress)
    {
        var removed = new List<string>();
        foreach (var entry in plan.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = journal.BeginStep(
                InstallerRunEffectKind.RuntimeRemoval,
                UninstallJournalDescriptions.Path(entry.Path));
            try
            {
                Delete(entry.Path);
                removed.Add(entry.Path);
                journal.CompleteStep(step, "Removed.", isReversible: false);
                progress?.Report(new(60 + (25 * removed.Count / Math.Max(plan.Paths.Count, 1)),
                    InstallerCulture.Pick("Removed ", "Удалено ") + entry.Path));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                journal.FailStep(step, exception.Message);
                failures.Add(new UninstallFailure(entry.Path, exception.Message));
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes one entry, refusing to follow a junction out of the tree and stepping around
    /// the directory this process is running from.
    ///
    /// A reparse point inside the runtime root is creatable by the same unprivileged user who
    /// owns it, and a recursive delete that followed one would take somebody else's directory
    /// with it. The link itself is removed; whatever it pointed at is not ours.
    ///
    /// The running directory is skipped rather than attempted, because it is normally
    /// <c>bin\uninstall</c> — the copy Apps &amp; features started — and Windows refuses to
    /// delete a running executable. Attempting it would turn the ordinary uninstall into a
    /// reported failure over the one file that could not possibly go yet; it is removed at the
    /// very end instead, or scheduled for the next restart.
    /// </summary>
    private void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete(recursive: false);
            return;
        }

        if (!Contains(path, selfDirectory))
        {
            directory.Delete(recursive: true);
            return;
        }

        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (!string.Equals(
                    Path.TrimEndingDirectorySeparator(child.FullName),
                    selfDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                Delete(child.FullName);
            }
        }

        // The directory itself only goes once its last child does, which for this branch is
        // after the uninstaller's own copy has been dealt with.
        if (!directory.EnumerateFileSystemInfos().Any())
        {
            directory.Delete(recursive: false);
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is that directory or lives inside it.</summary>
    private static bool Contains(string directory, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the directories the sweep had to step around and has now emptied.
    ///
    /// <c>bin</c> is the case: it is on the removal list, but it holds the running
    /// uninstaller, so the sweep takes everything else and leaves the shell behind. Once that
    /// copy has gone, the empty shell is a leftover of the mechanism rather than anything the
    /// person chose to keep. A directory that is still occupied — because the copy could only
    /// be scheduled — is left alone; the tail process removes what it holds.
    /// </summary>
    private static void RemoveDirectoriesLeftEmptyByTheSweep(
        UninstallPlan plan,
        IReadOnlyList<string> removed)
    {
        foreach (var entry in plan.Paths.Where(path => path.IsDirectory))
        {
            if (!removed.Contains(entry.Path, StringComparer.OrdinalIgnoreCase) ||
                !Directory.Exists(entry.Path))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(entry.Path).Any())
                {
                    Directory.Delete(entry.Path, recursive: false);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Already reported if it mattered: this pass only tidies what the sweep
                // deliberately left, and a shell it cannot remove changes nothing.
            }
        }
    }

    /// <summary>
    /// Removes the runtime root itself once the run has emptied it. A root with anything left
    /// in it — kept indexes, kept settings, the signing keys — stays, and so does one that lost
    /// a path to a failure.
    /// </summary>
    private bool RemoveEmptyRuntimeRoot(UninstallPlan plan, List<UninstallFailure> failures)
    {
        if (!plan.RemovesRuntimeRootEntirely ||
            failures.Count > 0 ||
            !Directory.Exists(layout.Root) ||
            Directory.EnumerateFileSystemEntries(layout.Root).Any())
        {
            return false;
        }

        try
        {
            Directory.Delete(layout.Root, recursive: false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new UninstallFailure(layout.Root, exception.Message));
            return false;
        }
    }

    private static string Detail(ProcessResult result)
    {
        if (result.TimedOut)
        {
            return "The stop request timed out.";
        }

        var message = result.StandardError.Trim();
        return message.Length > 0
            ? message
            : "The launcher exited with " + result.ExitCode + ".";
    }
}
