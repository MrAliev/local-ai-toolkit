using System.Text;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Activation;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Releases;
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
    bool AppsAndFeaturesEntryRemoved = false)
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
    IUserPathStore? userPath = null)
{
    private readonly TimeSpan stopTimeout = stopTimeout ?? TimeSpan.FromMinutes(2);

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
        cancellationToken.ThrowIfCancellationRequested();
        var removed = new List<string>();
        RemovePaths(
            plan.Paths.Where(entry => !entry.HoldsUninstaller).ToArray(),
            journal,
            failures,
            removed,
            cancellationToken,
            progress,
            (60, 85));
        progress?.Report(new(90, InstallerCulture.Pick(
            "Finishing the removal…",
            "Завершаю удаление…")));
        cancellationToken.ThrowIfCancellationRequested();
        // After the files: the PATH entry names a directory the binaries have already left.
        await RemoveLauncherPathAsync(plan, journal, failures);
        var (unregistered, copyStays) = RemoveRegistration(plan, journal, failures, cancellationToken);
        if (!copyStays)
        {
            RemovePaths(
                plan.Paths.Where(entry => entry.HoldsUninstaller).ToArray(),
                journal,
                failures,
                removed,
                cancellationToken,
                progress,
                (90, 99));
        }

        return new UninstallOutcome(
            removed,
            rewritten,
            hooks,
            failures,
            stopped,
            RemoveEmptyRuntimeRoot(plan, failures),
            unregistered);
    }

    /// <summary>
    /// Takes the launcher's directory off this account's PATH. An entry somebody removed by hand
    /// is not a failure, and a PATH that cannot be written is reported rather than thrown: it
    /// runs after the files, and an entry naming a directory that is gone is untidy rather than
    /// harmful.
    /// </summary>
    private async Task RemoveLauncherPathAsync(
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
            await LauncherPathRegistration.UnregisterAsync(directory, userPath);
            journal.CompleteStep(step, "Removed from the user PATH.", isReversible: false);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or InvalidOperationException)
        {
            journal.FailStep(step, exception.Message);
            failures.Add(new UninstallFailure(directory, exception.Message));
        }
    }

    /// <summary>
    /// Takes the Apps &amp; features entry out and then the uninstaller copy it runs, after every
    /// other path and the PATH entry, and only when all of them went: stopped or failed before
    /// this, the run leaves the entry and its copy, and Apps &amp; features still leads back to
    /// finishing it. An entry that was already gone is not a failure.
    ///
    /// Nothing is deferred. The removal never runs from this copy — it hands over to a
    /// temporary one first — so a copy that cannot be deleted is held by something else, and
    /// that is a failure. <c>CopyStays</c> then keeps the paths holding a copy.
    /// </summary>
    private (bool Unregistered, bool CopyStays) RemoveRegistration(
        UninstallPlan plan,
        InstallerRunJournal journal,
        List<UninstallFailure> failures,
        CancellationToken cancellationToken)
    {
        if (!plan.RemovesAppsAndFeaturesEntry || !OperatingSystem.IsWindows())
        {
            return (false, false);
        }

        if (failures.Count > 0)
        {
            return (false, true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var registration = new UninstallRegistration(layout, registrySubKey);
        var step = journal.BeginStep(
            InstallerRunEffectKind.RuntimeRemoval,
            UninstallJournalDescriptions.Registration(registration.UninstallerDirectory));
        var unregistered = false;
        try
        {
            unregistered = registration.Unregister();
            registration.RemoveUninstallerCopy();
            journal.CompleteStep(step, "Removed.", isReversible: false);
            return (unregistered, false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            journal.FailStep(step, exception.Message);
            failures.Add(new UninstallFailure(
                registration.UninstallerDirectory,
                exception.Message));
            return (unregistered, true);
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

    /// <summary><paramref name="percent"/> is the stretch of the bar these paths move it across.</summary>
    private static void RemovePaths(
        IReadOnlyList<RemovalPathEntry> entries,
        InstallerRunJournal journal,
        List<UninstallFailure> failures,
        List<string> removed,
        CancellationToken cancellationToken,
        IProgress<UninstallProgress>? progress,
        (int From, int To) percent)
    {
        var done = 0;
        foreach (var entry in entries)
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
                done++;
                progress?.Report(new(
                    percent.From + ((percent.To - percent.From) * done / entries.Count),
                    InstallerCulture.Pick("Removed ", "Удалено ") + entry.Path));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                journal.FailStep(step, exception.Message);
                failures.Add(new UninstallFailure(entry.Path, exception.Message));
            }
        }
    }

    /// <summary>
    /// Deletes one entry, refusing to follow a junction out of the tree.
    ///
    /// A reparse point inside the runtime root is creatable by the same unprivileged user who
    /// owns it, and a recursive delete that followed one would take somebody else's directory
    /// with it. The link itself is removed; whatever it pointed at is not ours.
    /// </summary>
    private static void Delete(string path)
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

        directory.Delete(recursive: true);
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
