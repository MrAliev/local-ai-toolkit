using LocalAi.TestFixtures;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Releases;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Performing the plan: exactly what the preview named, in the one order that is safe, with
/// every effect journalled and every failure reported rather than swallowed.
/// </summary>
public sealed class UninstallRunnerTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string journalDirectory = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalJournals",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A key of this test's own, so nothing here reads or removes the real Apps &amp; features
    /// entry of a machine that genuinely has LocalAi installed.
    /// </summary>
    private readonly string registrySubKey =
        @"Software\LocalAi.Tests\" + Guid.NewGuid().ToString("N") + @"\Uninstall\LocalAi";

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\LocalAi.Tests\" + registrySubKey.Split('\\')[2],
                throwOnMissingSubKey: false);
            Directory.Delete(journalDirectory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    [Fact]
    public async Task Apply_removes_exactly_what_the_preview_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var named = plan.Paths.Select(entry => entry.Path).ToArray();

        var outcome = await Apply(plan);

        Assert.True(outcome.Succeeded);
        Assert.Equal(named, outcome.RemovedPaths);
        Assert.All(named, path => Assert.False(
            File.Exists(path) || Directory.Exists(path),
            path + " should be gone"));
        // The one thing a default full uninstall keeps, still whole.
        Assert.True(File.Exists(Path.Combine(
            machine.Runtime,
            RemovalMatrix.SigningKeyDirectoryName,
            "private.key")));
    }

    [Fact]
    public async Task A_reinstall_friendly_run_leaves_the_indexes_and_settings_byte_identical()
    {
        var repositories = Path.Combine(machine.Runtime, "repositories");
        var before = RemovalFixture.Snapshot(repositories);
        var settings = RemovalMatrix.SettingsFileNames
            .ToDictionary(
                name => name,
                name => File.ReadAllBytes(Path.Combine(machine.Runtime, name)));

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly)));

        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.False(outcome.RuntimeRootRemoved);
        Assert.Equal(before, RemovalFixture.Snapshot(repositories));
        foreach (var (name, content) in settings)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(machine.Runtime, name)));
        }
    }

    [Fact]
    public async Task Disconnecting_clients_leaves_the_runtime_untouched()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var runner = new RecordingProcessRunner();

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)),
            runner);

        Assert.True(outcome.Succeeded);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        // Nothing of the runtime is going, so interrupting a broker that may be mid-inference
        // would be a gratuitous stop.
        Assert.Empty(runner.Calls);
        Assert.False(outcome.ProcessesStopped);
    }

    /// <summary>
    /// The order the whole run depends on. Deleting the files underneath a running broker
    /// leaves a half-removed tree and a process still holding what is left of it, so the stop
    /// is observed to happen while everything is still there — a condition, not a delay.
    /// </summary>
    [Fact]
    public async Task The_broker_is_asked_to_finish_before_the_root_is_touched()
    {
        var runtimeIntactAtStop = false;
        var runner = new RecordingProcessRunner(() =>
            runtimeIntactAtStop = Directory.Exists(Path.Combine(machine.Runtime, "bin")) &&
                Directory.Exists(Path.Combine(machine.Runtime, "jobs")));

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)),
            runner);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(machine.LauncherPath, call.Executable);
        Assert.Equal(["stop"], call.Arguments);
        Assert.True(runtimeIntactAtStop, "the stop must come before anything is removed");
        Assert.True(outcome.ProcessesStopped);
    }

    [Fact]
    public async Task A_broker_that_will_not_stop_refuses_the_run_before_anything_is_removed()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var runner = new RecordingProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "broker_still_running", false, false),
        };
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var refusal = await Assert.ThrowsAsync<UninstallRefusedException>(() =>
            Runner(runner).ApplyAsync(plan, journal, TestContext.Current.CancellationToken));

        Assert.Contains("nothing was removed", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("broker_still_running", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        var step = Assert.Single(journal.Snapshot.Steps);
        Assert.Equal(InstallerRunEffectKind.ProcessStop, step.Kind);
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
    }

    /// <summary>
    /// A broken installation is exactly the one somebody most needs to clear up, so a missing
    /// launcher is not a reason to refuse — there is nothing left to ask.
    /// </summary>
    [Fact]
    public async Task A_missing_launcher_does_not_block_the_removal()
    {
        File.Delete(machine.LauncherPath);
        var runner = new RecordingProcessRunner();

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)),
            runner);

        Assert.Empty(runner.Calls);
        Assert.False(outcome.ProcessesStopped);
        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
    }

    /// <summary>
    /// A pointer the launcher cannot read stops it before it looks at a single process, so its
    /// refusal says nothing about what is running. Each version directory is asked by name.
    /// </summary>
    [Fact]
    public async Task A_broken_pointer_is_stopped_version_by_version_instead_of_refusing()
    {
        File.WriteAllText(Path.Combine(machine.Runtime, "bin", "current.json"), "{ not json");
        var runner = new RecordingProcessRunner
        {
            Answer = arguments => arguments is ["stop"]
                ? Exit(1, "current_pointer_invalid: LocalAi current-version pointer is invalid.")
                : Exit(0),
        };
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var outcome = await Runner(runner).ApplyAsync(
            plan,
            journal,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Contains(
            runner.Calls,
            call => call.Arguments.SequenceEqual(["stop", "--version", RemovalFixture.InstalledVersion]));
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.Equal(InstallerRunStepStatus.Completed, journal.Snapshot.Steps[0].Status);
    }

    [Fact]
    public async Task A_pointer_to_a_version_that_is_gone_does_not_block_the_removal()
    {
        Directory.Delete(
            Path.Combine(machine.Runtime, "bin", "versions", RemovalFixture.InstalledVersion),
            recursive: true);
        var runner = new RecordingProcessRunner
        {
            Answer = _ => Exit(1, "version_incomplete: LocalAi version '0.1.50' does not exist."),
        };

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)),
            runner);

        Assert.True(outcome.Succeeded);
        Assert.Single(runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
    }

    /// <summary>
    /// A version directory missing a file is one the launcher cannot stop, and something may be
    /// running out of it: the refusal stands.
    /// </summary>
    [Fact]
    public async Task A_version_the_launcher_cannot_validate_is_still_refused()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var runner = new RecordingProcessRunner
        {
            Answer = _ => Exit(1, "version_incomplete: LocalAi version '0.1.50' is missing 'x.exe'."),
        };
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var refusal = await Assert.ThrowsAsync<UninstallRefusedException>(() =>
            Runner(runner).ApplyAsync(plan, journal, TestContext.Current.CancellationToken));

        Assert.Contains("nothing was removed", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("version_incomplete", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
    }

    /// <summary>A run that asks nothing of the launcher does not say it is asking.</summary>
    [Fact]
    public async Task A_run_that_stops_nothing_does_not_say_it_is_stopping()
    {
        var seen = new RecordedProgress();

        await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)),
            progress: seen);

        Assert.NotEmpty(seen.Texts);
        Assert.DoesNotContain(seen.Texts, text => text.Contains("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_runtime_root_itself_goes_once_the_run_has_emptied_it()
    {
        var outcome = await Apply(await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true)));

        Assert.True(outcome.RuntimeRootRemoved);
        Assert.False(Directory.Exists(machine.Runtime));
        // The installer's record of what it did lives outside the root and outlives it.
        Assert.True(Directory.Exists(journalDirectory));
    }

    [Fact]
    public async Task Client_files_are_rewritten_on_disk_with_the_users_text_intact()
    {
        var claudeInstructions = Path.Combine(machine.Home, ".claude", "CLAUDE.md");
        var codexConfiguration = Path.Combine(machine.Home, ".codex", "config.toml");

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(
            RemovalFixture.ClaudeInstructionsPreamble,
            File.ReadAllText(claudeInstructions));
        var codex = File.ReadAllText(codexConfiguration);
        Assert.DoesNotContain("mcp_servers.codesearch", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.locallm", codex, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", codex, StringComparison.Ordinal);
        Assert.Equal(["Claude", "Codex"], outcome.DisconnectedConfigurations);
    }

    [Fact]
    public async Task Removing_a_dispatcher_puts_back_the_hook_it_was_chaining()
    {
        var chained = Path.Combine(machine.PlainHooks, "post-commit");
        var foreign = Path.Combine(machine.PlainHooks, "post-checkout");

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)
                .With(RemovalItem.GitHooks, true)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(RemovalFixture.ChainedHookBody, File.ReadAllText(chained));
        Assert.False(File.Exists(chained + ".pre-localai"));
        Assert.False(File.Exists(Path.Combine(machine.PlainHooks, "post-merge")));
        Assert.Equal(RemovalFixture.ForeignHookBody, File.ReadAllText(foreign));
    }

    [Fact]
    public async Task The_exclude_file_loses_our_lines_and_keeps_everybody_elses()
    {
        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients)));

        Assert.True(outcome.Succeeded);
        Assert.Equal(
            "# git ls-files --others --exclude-from=.git/info/exclude\n" +
            "node_modules/\n",
            File.ReadAllText(machine.HuskyExclude));
        Assert.False(File.Exists(Path.Combine(machine.HuskyHooks, "post-commit")));
        // husky's own runner directory is not ours and stays exactly as it is.
        Assert.True(File.Exists(Path.Combine(machine.HuskyHooks, "_", "h")));
    }

    [Fact]
    public async Task A_repository_that_no_longer_exists_costs_the_run_nothing()
    {
        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)));

        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain(
            outcome.RemovedHooks,
            path => path.Contains("deleted-repository", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One path another program is holding open stops that path, not the uninstall. The
    /// alternative — abandoning the run at the first locked file — leaves more behind than it
    /// removes and tells the person less about it.
    /// </summary>
    [Fact]
    public async Task A_path_something_else_is_holding_is_reported_rather_than_hidden()
    {
        var held = Path.Combine(machine.Runtime, "jobs", "job.json");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = await Apply(
            await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall)));

        Assert.False(outcome.Succeeded);
        var failure = Assert.Single(outcome.Failures);
        Assert.Equal(Path.Combine(machine.Runtime, "jobs"), failure.Path);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        Assert.False(outcome.RuntimeRootRemoved);
    }

    /// <summary>
    /// The journal answers "what is there now" for a machine an uninstall half-changed, so the
    /// intent is on disk before each effect runs. None of it is offered back: an uninstall is
    /// not a transaction, and a rollback offering to restore a tree it no longer has would be
    /// a promise nothing can keep.
    /// </summary>
    [Fact]
    public async Task Every_effect_is_journalled_intent_first_and_none_is_offered_back()
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        await Runner(new RecordingProcessRunner()).ApplyAsync(
            plan,
            journal,
            TestContext.Current.CancellationToken);

        var steps = journal.Snapshot.Steps;
        Assert.Equal(InstallerRunEffectKind.ProcessStop, steps[0].Kind);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.AgentConfiguration);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.GitHookRemoval);
        Assert.Contains(steps, step => step.Kind == InstallerRunEffectKind.RuntimeRemoval);
        Assert.All(steps, step =>
        {
            Assert.Equal(InstallerRunStepStatus.Completed, step.Status);
            Assert.False(step.IsReversible);
        });
        Assert.False(journal.Snapshot.HasReversibleWork);
        foreach (var entry in plan.Paths)
        {
            Assert.Contains(
                steps,
                step => step.Description.Contains(entry.Path, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Removal has to take the Apps &amp; features entry with it: one that outlives the
    /// installation offers to uninstall something that is not there.
    /// </summary>
    [Fact]
    public async Task The_apps_and_features_entry_goes_with_the_binaries()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        Assert.True(plan.RemovesAppsAndFeaturesEntry);
        Assert.Contains("Apps & features entry", plan.PreviewText, StringComparison.Ordinal);

        var outcome = await Apply(plan);

        Assert.True(outcome.AppsAndFeaturesEntryRemoved);
        Assert.Null(registration.Read());
        Assert.False(Directory.Exists(registration.UninstallerDirectory));
    }

    /// <summary>
    /// The removal half of a clean reinstall takes the binaries, and the uninstaller copy the
    /// entry runs lives among them. Kept, the entry pointed at a file that no longer existed
    /// until the installation half wrote it again — and for good if the person closed the
    /// wizard there. It goes with the copy, named on the review page like any other removal,
    /// and the installation half registers it again.
    /// </summary>
    [Fact]
    public async Task A_reinstall_takes_the_entry_with_the_copy_it_points_at()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var selection = RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly);
        Assert.True(selection.Includes(RemovalItem.Binaries));

        var plan = await machine.PlanAsync(
            selection,
            TestContext.Current.CancellationToken,
            registrySubKey,
            installationFollows: true);
        var outcome = await Apply(plan);

        Assert.True(outcome.Succeeded);
        Assert.False(File.Exists(registration.UninstallerPath));
        Assert.Null(registration.Read());
        Assert.True(plan.RemovesAppsAndFeaturesEntry);
        Assert.Contains("Apps & features entry", plan.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stopped between two paths, a removal leaves whatever it has not reached yet, and the
    /// entry with it: Apps &amp; features is the way back to finishing, and the copy it runs is
    /// still there to run.
    /// </summary>
    [Fact]
    public async Task Stopping_after_the_binaries_went_leaves_the_entry_and_the_copy_it_runs()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var bin = machine.Layout.BinRoot;
        using var journal = InstallerRunJournal.Start(journalDirectory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(new RecordingProcessRunner()).ApplyAsync(
                plan,
                journal,
                stop.Token,
                new SynchronousProgress(_ =>
                {
                    if (!Directory.Exists(bin))
                    {
                        stop.Cancel();
                    }
                })));

        Assert.False(Directory.Exists(bin));
        AssertTheEntryStillRunsItsCopy(registration);
    }

    /// <summary>
    /// A path that could not be removed is a removal to finish later, and the entry is how it is
    /// found again. It stays, and so does the directory holding the copy it runs.
    /// </summary>
    [Fact]
    public async Task A_path_that_could_not_be_removed_leaves_the_entry_and_the_copy_it_runs()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        using var held = new FileStream(
            Path.Combine(machine.Runtime, "jobs", "job.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        using var journal = InstallerRunJournal.Start(journalDirectory);

        var outcome = await Runner(new RecordingProcessRunner()).ApplyAsync(
            plan,
            journal,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.AppsAndFeaturesEntryRemoved);
        Assert.False(Directory.Exists(machine.Layout.BinRoot));
        AssertTheEntryStillRunsItsCopy(registration);
        Assert.DoesNotContain(
            journal.Snapshot.Steps,
            step => step.Description ==
                UninstallJournalDescriptions.Registration(registration.UninstallerDirectory));
    }

    /// <summary>
    /// A copy parked in bin by an earlier release is what that release's entry runs, so bin
    /// waits for the entry like the directory holding a copy parked by this one.
    /// </summary>
    [Fact]
    public async Task A_copy_an_earlier_release_parked_in_bin_keeps_bin_until_the_entry_goes()
    {
        var legacy = Path.Combine(machine.Layout.BinRoot, "uninstall", "LocalAi.Installer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "earlier installer");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(registrySubKey, writable: true))
        {
            key.SetValue("DisplayName", UninstallRegistration.DisplayName);
            key.SetValue("UninstallString", "\"" + legacy + "\" --uninstall");
            key.SetValue("InstallLocation", machine.Runtime);
        }

        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        using var held = new FileStream(
            Path.Combine(machine.Runtime, "jobs", "job.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var outcome = await Apply(plan);

        Assert.False(outcome.Succeeded);
        AssertTheEntryStillRunsItsCopy(registration);
    }

    /// <summary>
    /// The order a stopped removal depends on: every other path, then the PATH entry, then the
    /// Apps &amp; features entry, and only then the directory holding the copy that entry runs.
    /// </summary>
    [Fact]
    public async Task The_entry_goes_after_every_other_path_and_the_path_entry()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            registrySubKey,
            readUserPath: () => machine.Layout.LauncherDirectory);
        Assert.True(plan.RemovesLauncherFromPath);
        var holder = plan.Paths.Single(entry =>
            registration.UninstallerDirectory.StartsWith(
                entry.Path + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)).Path;
        using var journal = InstallerRunJournal.Start(journalDirectory);

        var outcome = await new UninstallRunner(
                machine.Layout,
                new RecordingProcessRunner(),
                registrySubKey: registrySubKey,
                userPath: new UserPath(machine.Layout.LauncherDirectory))
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.AppsAndFeaturesEntryRemoved);
        var steps = journal.Snapshot.Steps.Select(step => step.Description).ToList();
        int At(string description)
        {
            Assert.Contains(description, steps);
            return steps.IndexOf(description);
        }

        var entry = At(UninstallJournalDescriptions.Registration(registration.UninstallerDirectory));
        Assert.True(
            entry > At(UninstallJournalDescriptions.LauncherPath(machine.Layout.LauncherDirectory)),
            "the entry went before the PATH step");
        Assert.All(
            plan.Paths.Where(path => path.Path != holder),
            path => Assert.True(
                entry > At(UninstallJournalDescriptions.Path(path.Path)),
                "the entry went before " + path.Path));
        Assert.True(
            At(UninstallJournalDescriptions.Path(holder)) > entry,
            "the copy the entry runs went before the entry");
        Assert.Null(registration.Read());
        Assert.False(Directory.Exists(holder));
    }

    /// <summary>Stopped as late as the PATH step, the run still leaves the entry.</summary>
    [Fact]
    public async Task Stopping_at_the_path_step_leaves_the_entry_and_the_copy_it_runs()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            TestContext.Current.CancellationToken,
            registrySubKey,
            readUserPath: () => machine.Layout.LauncherDirectory);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var journal = InstallerRunJournal.Start(journalDirectory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UninstallRunner(
                    machine.Layout,
                    new RecordingProcessRunner(),
                    registrySubKey: registrySubKey,
                    userPath: new UserPath(machine.Layout.LauncherDirectory, stop.Cancel))
                .ApplyAsync(plan, journal, stop.Token));

        AssertTheEntryStillRunsItsCopy(registration);
    }

    /// <summary>
    /// Nothing schedules a deletion for later — a per-user installation cannot ask for one at
    /// the next restart — so a copy that cannot be deleted now is a failure, reported as one.
    /// The directory holding it is not started, so no entry is left running a copy that is gone.
    /// </summary>
    [Fact]
    public async Task A_copy_that_cannot_be_deleted_is_a_failure_and_the_directory_holding_it_stays()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        using var held = new FileStream(
            registration.UninstallerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var journal = InstallerRunJournal.Start(journalDirectory);

        var outcome = await Runner(new RecordingProcessRunner()).ApplyAsync(
            plan,
            journal,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        var step = Assert.Single(
            journal.Snapshot.Steps,
            step => step.Description ==
                UninstallJournalDescriptions.Registration(registration.UninstallerDirectory));
        Assert.Equal(InstallerRunStepStatus.Failed, step.Status);
        Assert.All(journal.Snapshot.Steps, step => Assert.DoesNotContain(
            "restart",
            step.Detail ?? string.Empty,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(registration.UninstallerPath));
        Assert.True(
            registration.Read() is null || File.Exists(registration.UninstallerPath),
            "the entry outlived the uninstaller it runs");
    }

    /// <summary>The entry is still there, and the file its command line starts is too.</summary>
    private static void AssertTheEntryStillRunsItsCopy(UninstallRegistration registration)
    {
        var entry = registration.Read();
        Assert.NotNull(entry);
        var command = entry.UninstallString;
        Assert.StartsWith("\"", command, StringComparison.Ordinal);
        var copy = command[1..command.IndexOf('"', 1)];
        Assert.True(File.Exists(copy), "the entry runs " + copy + ", which is gone");
    }

    /// <summary>
    /// The same selection with nothing following still takes the entry: an entry beside an
    /// installation whose binaries are gone offers to uninstall what is not there.
    /// </summary>
    [Fact]
    public async Task The_same_removal_alone_still_takes_the_entry()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);

        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly),
            registrySubKey);

        Assert.True(plan.RemovesAppsAndFeaturesEntry);
    }

    /// <summary>
    /// The key is one per account, not one per installation, so the entry found under it may
    /// describe a different LocalAi — and removing that one is removing somebody's way to
    /// uninstall a thing this run never touched. It is also what every removal run in a test
    /// did to the machine it ran on: a temporary installation, and the real entry beside it.
    /// </summary>
    [Fact]
    public async Task An_entry_that_describes_another_installation_is_left_alone()
    {
        using var elsewhere = new RemovalFixture();
        var theirs = new UninstallRegistration(elsewhere.Layout, registrySubKey);
        theirs.Register(RemovalFixture.InstalledVersion, elsewhere.LauncherPath);

        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.FullUninstall),
            registrySubKey);
        var outcome = await Apply(plan);

        Assert.False(plan.RemovesAppsAndFeaturesEntry);
        Assert.DoesNotContain("Apps & features entry", plan.PreviewText, StringComparison.Ordinal);
        Assert.False(outcome.AppsAndFeaturesEntryRemoved);
        Assert.Equal(elsewhere.Layout.Root, theirs.Read()?.InstallLocation);
    }

    [Fact]
    public async Task Disconnecting_clients_leaves_the_entry_where_it_is()
    {
        var registration = new UninstallRegistration(machine.Layout, registrySubKey);
        registration.Register(RemovalFixture.InstalledVersion, machine.LauncherPath);

        var plan = await Plan(
            RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            registrySubKey);
        await Apply(plan);

        Assert.False(plan.RemovesAppsAndFeaturesEntry);
        Assert.NotNull(registration.Read());
    }

    /// <summary>
    /// The removal says where it has got to. It used to say nothing between starting and
    /// finishing, and the broker stop alone can take two minutes — a bar that has not moved
    /// for two minutes is what makes people kill installers.
    /// </summary>
    [Fact]
    public async Task The_removal_says_where_it_has_got_to()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var seen = new List<UninstallProgress>();

        await Apply(plan, progress: new Progress<UninstallProgress>(seen.Add));

        // Reported in order, ending short of 100: finishing is the caller's to announce,
        // because only the caller knows whether anything follows this removal.
        Assert.NotEmpty(seen);
        Assert.Equal(seen.Select(step => step.Percent).Order(), seen.Select(step => step.Percent));
        Assert.All(seen, step => Assert.InRange(step.Percent, 0, 99));
        Assert.All(seen, step => Assert.False(string.IsNullOrWhiteSpace(step.Text)));
        Assert.Contains(seen, step => step.Text.Contains("stop", StringComparison.Ordinal));
        Assert.Contains(seen, step => step.Text.Contains("Removing files", StringComparison.Ordinal));
    }

    /// <summary>The path removed after the entry does not send the bar backwards.</summary>
    [Fact]
    public async Task The_removal_moves_forward_past_the_entry_too()
    {
        new UninstallRegistration(machine.Layout, registrySubKey)
            .Register(RemovalFixture.InstalledVersion, machine.LauncherPath);
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall), registrySubKey);
        var seen = new List<UninstallProgress>();

        await Apply(plan, progress: new SynchronousProgress(seen.Add));

        Assert.Equal(seen.Select(step => step.Percent).Order(), seen.Select(step => step.Percent));
        Assert.All(seen, step => Assert.InRange(step.Percent, 0, 99));
        Assert.Contains(seen, step => step.Text.EndsWith(plan.Paths[^1].Path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing reported is a supported caller, not a crash: the standalone runner and every
    /// test that predates this pass no progress at all.
    /// </summary>
    [Fact]
    public async Task A_caller_that_wants_no_progress_gets_none()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var outcome = await Apply(plan);

        Assert.True(outcome.Succeeded);
    }

    private Task<UninstallPlan> Plan(
        RemovalSelection selection,
        string? registrySubKey = null) =>
        machine.PlanAsync(selection, TestContext.Current.CancellationToken, registrySubKey);

    private async Task<UninstallOutcome> Apply(
        UninstallPlan plan,
        RecordingProcessRunner? runner = null,
        IProgress<UninstallProgress>? progress = null)
    {
        using var journal = InstallerRunJournal.Start(journalDirectory);
        return await Runner(runner ?? new RecordingProcessRunner())
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken, progress);
    }

    private UninstallRunner Runner(IProcessRunner runner) =>
        new(machine.Layout, runner, registrySubKey: registrySubKey);

    /// <summary>
    /// Stands in for the launcher. <paramref name="onCall"/> runs at the moment the stop is
    /// requested, which is how the ordering these tests care about is observed rather than
    /// assumed.
    /// </summary>
    private sealed class RecordingProcessRunner(Action? onCall = null) : IProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public ProcessResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, false);

        /// <summary>An answer per command line, for a launcher asked more than one thing.</summary>
        public Func<IReadOnlyList<string>, ProcessResult>? Answer { get; set; }

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            onCall?.Invoke();
            Calls.Add((executable, arguments));
            return Task.FromResult(Answer?.Invoke(arguments) ?? Result);
        }
    }

    private static ProcessResult Exit(int code, string error = "") =>
        new(code, string.Empty, error, false, false);

    private sealed class UserPath(string value, Action? onWrite = null) : IUserPathStore
    {
        private string? value = value;

        public string? Read() => value;

        public void Write(string written)
        {
            value = written;
            onWrite?.Invoke();
        }
    }

    /// <summary>Synchronous, so what was reported is all there when the run returns.</summary>
    private sealed class RecordedProgress : IProgress<UninstallProgress>
    {
        public List<string> Texts { get; } = [];

        public void Report(UninstallProgress value) => Texts.Add(value.Text);
    }

    /// <summary>Runs inside the report, so the run sees what it did before its next step.</summary>
    private sealed class SynchronousProgress(Action<UninstallProgress> report)
        : IProgress<UninstallProgress>
    {
        public void Report(UninstallProgress value) => report(value);
    }
}
