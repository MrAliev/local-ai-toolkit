using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The wizard in uninstall mode, over the same installed machine the removal core is tested
/// against. What these pin is the promise the page makes: nothing is removed before the
/// confirmation, and what the review page listed is what runs.
/// </summary>
public sealed class UninstallWizardViewModelTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    private readonly string journalDirectory = Path.Combine(
        Path.GetTempPath(),
        "LocalAi.RemovalWizard",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        machine.Dispose();
        try
        {
            Directory.Delete(journalDirectory, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task The_page_opens_on_a_full_uninstall_with_the_keys_untouched()
    {
        var wizard = await Wizard();

        Assert.Equal(RemovalPreset.FullUninstall, wizard.SelectedPreset);
        Assert.All(wizard.Rows, row => Assert.True(row.IsSelected, row.Title));
        Assert.False(wizard.RemoveSigningKeys);
        Assert.DoesNotContain(wizard.Rows, row => row.Item == RemovalItem.SigningKeys);
        Assert.Contains("only copy", wizard.SigningKeysWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_preset_refills_the_boxes_and_says_what_it_left_open()
    {
        var wizard = await Wizard();

        wizard.SelectedPreset = RemovalPreset.ReinstallFriendly;

        Assert.True(Row(wizard, RemovalItem.Binaries).IsSelected);
        Assert.False(Row(wizard, RemovalItem.RepositoryIndexes).IsSelected);
        Assert.False(Row(wizard, RemovalItem.Settings).IsSelected);
        // The three client rows used to arrive here undecided. They are pinned to kept now:
        // all three call the launcher a reinstall puts back, so asking was asking twice.
        Assert.False(Row(wizard, RemovalItem.ClaudeIntegration).IsSelected);
        Assert.False(Row(wizard, RemovalItem.CodexIntegration).IsSelected);
        Assert.False(Row(wizard, RemovalItem.GitHooks).IsSelected);
        Assert.All(wizard.Rows, row => Assert.False(row.NeedsDecision, row.Title));
    }

    /// <summary>
    /// The sentence that replaces the "your choice" hint the pinning took away — one wording
    /// for each way of arriving at this preset, because on one of them an installation follows
    /// and on the other nothing does.
    /// </summary>
    [Fact]
    public async Task A_reinstall_says_what_it_keeps_works_again_once_the_launcher_is_back()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.HasKeepNotice);
        Assert.StartsWith(
            "The Claude and Codex registrations and the Git hook dispatchers are left exactly " +
            "as they are. All three call the launcher this run removes, and work again once " +
            "the installation that follows puts it back.",
            wizard.KeepNotice,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// With the binaries row unticked the launcher stays, so nothing the notice keeps stops
    /// working and the sentence about the launcher has nothing to be about.
    /// </summary>
    [Fact]
    public async Task A_reinstall_that_keeps_the_binaries_says_nothing_about_the_launcher()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        Row(wizard, RemovalItem.Binaries).IsSelected = false;

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "The Claude and Codex registrations and the Git hook dispatchers are left exactly " +
            "as they are.",
            wizard.KeepNotice);
    }

    /// <summary>
    /// Picked by hand out of a plain uninstall, nothing follows — so the sentence says where
    /// the decision went instead of promising a rewrite that is not coming.
    /// </summary>
    [Fact]
    public async Task Picking_the_preset_by_hand_is_told_where_the_choice_went()
    {
        var wizard = await Wizard(preset: RemovalPreset.ReinstallFriendly);

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.HasKeepNotice);
        Assert.Contains("go back and tick their rows", wizard.KeepNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("follows", wizard.KeepNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice lists what this preset keeps, and the hooks LocalAi wrote into Claude Code's
    /// settings are not among them: they point at the launcher, which the same preset deletes.
    /// That is the text-search gate and the worktree pair alike, so the sentence names neither
    /// alone.
    /// </summary>
    [Fact]
    public async Task A_reinstall_says_the_claude_hooks_go_even_though_claude_is_kept()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.EndsWith(
            " The hooks LocalAi wrote into Claude Code's settings.json are not among them: " +
            "they go with the launcher, and the installation that follows writes them again " +
            "from what you choose there.",
            wizard.KeepNotice,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And when nothing follows, the same fact without the promise: no installation is coming
    /// to write them again, so the sentence must not imply one.
    /// </summary>
    [Fact]
    public async Task Picking_the_preset_by_hand_is_told_the_claude_hooks_go_too()
    {
        var wizard = await Wizard(preset: RemovalPreset.ReinstallFriendly);

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.EndsWith(
            " The hooks LocalAi wrote into Claude Code's settings.json still go: they point at " +
            "the launcher, which this run removes.",
            wizard.KeepNotice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("follows", wizard.KeepNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice reads the rows, not the preset, so its reason must not credit the preset:
    /// here a full uninstall is still chosen and would disconnect both clients.
    /// </summary>
    [Fact]
    public async Task Unticking_both_clients_by_hand_is_not_credited_to_the_preset()
    {
        var wizard = await Wizard();
        Row(wizard, RemovalItem.ClaudeIntegration).IsSelected = false;
        Row(wizard, RemovalItem.CodexIntegration).IsSelected = false;

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RemovalPreset.FullUninstall, wizard.SelectedPreset);
        Assert.StartsWith(
            "The Claude and Codex registrations are left exactly as they are: this removal " +
            "disconnects no clients.",
            wizard.KeepNotice,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A row ticked by hand outranks the preset that filled it in, and the sentence has to
    /// stop rather than describe a run that is not happening.
    /// </summary>
    [Fact]
    public async Task Overriding_a_pinned_row_silences_the_sentence()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        Row(wizard, RemovalItem.ClaudeIntegration).IsSelected = true;

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.False(wizard.HasKeepNotice);
        Assert.Equal(string.Empty, wizard.KeepNotice);
    }

    /// <summary>
    /// A full uninstall removes those rows outright; there is nothing kept to explain.
    /// </summary>
    [Fact]
    public async Task A_full_uninstall_has_no_such_sentence()
    {
        var wizard = await Wizard();

        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));

        Assert.False(wizard.HasKeepNotice);
    }

    [Fact]
    public async Task A_row_changed_by_hand_is_what_the_plan_uses()
    {
        var wizard = await Wizard();
        wizard.SelectedPreset = RemovalPreset.DisconnectClients;
        Row(wizard, RemovalItem.Binaries).IsSelected = true;
        Row(wizard, RemovalItem.CodexIntegration).IsSelected = false;

        var selection = wizard.Selection();

        Assert.True(selection.Includes(RemovalItem.Binaries));
        Assert.True(selection.Includes(RemovalItem.ClaudeIntegration));
        Assert.False(selection.Includes(RemovalItem.CodexIntegration));
        Assert.False(selection.Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public async Task The_keys_reach_the_plan_only_through_their_own_checkbox()
    {
        var wizard = await Wizard();

        Assert.False(wizard.Selection().Includes(RemovalItem.SigningKeys));

        wizard.RemoveSigningKeys = true;

        Assert.True(wizard.Selection().Includes(RemovalItem.SigningKeys));
    }

    [Fact]
    public async Task The_connected_repositories_are_listed_before_anything_is_chosen()
    {
        var wizard = await Wizard();

        Assert.Equal(
            ["gone", "husky", "plain"],
            wizard.Repositories.Select(repository => repository.RepositoryId).Order());
        var missing = wizard.Repositories.Single(repository => repository.RepositoryId == "gone");
        Assert.False(missing.CanChoose);
        Assert.False(missing.IsSelected);
        Assert.Contains("skipped", missing.StateText, StringComparison.Ordinal);
        // Counted after the label rather than through a "(s)" plural, which no translation
        // survives and which read as "1 thing(s)" in English too.
        Assert.Contains(
            "hooks installed: 2",
            wizard.Repositories.Single(repository => repository.RepositoryId == "plain").StateText,
            StringComparison.Ordinal);
        // Found where core.hooksPath sends the search rather than in $GIT_DIR/hooks: a wizard
        // that looked only in the default place would report this repository as clean.
        var husky = wizard.Repositories.Single(repository => repository.RepositoryId == "husky");
        Assert.Contains("hooks installed: 1", husky.StateText, StringComparison.Ordinal);
        Assert.True(husky.IsSelected);
    }

    [Fact]
    public async Task A_repository_can_be_left_out_on_its_own()
    {
        var wizard = await Wizard();
        wizard.Repositories.Single(repository => repository.RepositoryId == "plain")
            .IsSelected = false;

        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(machine.PlainHooks, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Contains(machine.HuskyHooks, wizard.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule the whole page exists for: reaching the review page changes nothing on disk,
    /// and the Uninstall button stays out of reach until the person says they have read it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_removed_before_the_confirmation()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var wizard = await Wizard();

        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UninstallPage.Confirm, wizard.CurrentPage);
        Assert.Contains(machine.Runtime, wizard.PreviewText, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
        Assert.False(wizard.CanRun);

        wizard.IsConfirmed = true;

        Assert.True(wizard.CanRun);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
    }

    [Fact]
    public async Task Going_back_takes_the_confirmation_with_it()
    {
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(wizard.MovePrevious());

        Assert.Equal(UninstallPage.Choose, wizard.CurrentPage);
        Assert.False(wizard.IsConfirmed);
        Assert.False(wizard.CanRun);
    }

    [Fact]
    public async Task The_run_performs_what_the_review_page_listed()
    {
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;
        var preview = wizard.PreviewText;

        var succeeded = await wizard.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(succeeded);
        Assert.Equal(UninstallPage.Finish, wizard.CurrentPage);
        Assert.False(Directory.Exists(Path.Combine(machine.Runtime, "bin")));
        // Kept, and the preview said so, because a full uninstall does not take the keys.
        Assert.True(Directory.Exists(Path.Combine(
            machine.Runtime,
            RemovalMatrix.SigningKeyDirectoryName)));
        Assert.Contains("keep", preview, StringComparison.Ordinal);
        Assert.Contains("winget uninstall", wizard.Report, StringComparison.Ordinal);
        Assert.Contains("ollama rm", wizard.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_stop_ends_the_run_saying_the_machine_is_unchanged()
    {
        var before = RemovalFixture.Snapshot(machine.Runtime);
        var wizard = await Wizard(new StubProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "broker_still_running", false, false),
        });
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        var succeeded = await wizard.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(succeeded);
        Assert.True(wizard.HasRunError);
        Assert.Equal(UninstallPage.Finish, wizard.CurrentPage);
        Assert.Contains("nothing was removed", wizard.Summary, StringComparison.Ordinal);
        Assert.Equal(before, RemovalFixture.Snapshot(machine.Runtime));
    }

    /// <summary>
    /// The failed step of a refused run is the stop request, which removes nothing, so the
    /// report calls it failed rather than a removal that may have left partial changes — on the
    /// line after saying nothing was removed.
    /// </summary>
    [Fact]
    public async Task A_refused_stop_is_reported_as_failed_rather_than_as_a_partial_removal()
    {
        var wizard = await Wizard(new StubProcessRunner
        {
            Result = new ProcessResult(1, string.Empty, "broker_still_running", false, false),
        });
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith(
            "Nothing was removed.\nFailed: stopping LocalAi's running tools and broker — broker_still_running" +
                Environment.NewLine,
            wizard.Report,
            StringComparison.Ordinal);
        Assert.DoesNotContain("partial changes", wizard.Report, StringComparison.Ordinal);
    }

    /// <summary>
    /// By then the run has usually rewritten client configurations and deleted dispatchers, so
    /// a partial failure counts those as the success summary does rather than denying them.
    /// </summary>
    [Fact]
    public async Task A_partial_removal_counts_the_configurations_and_hooks_it_changed()
    {
        var held = Path.Combine(machine.Runtime, "jobs", "job.json");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("Could not be removed: 1; removed: ", wizard.Summary, StringComparison.Ordinal);
        Assert.Contains(" Client configurations updated: ", wizard.Summary, StringComparison.Ordinal);
        Assert.Contains(" Git hook dispatchers removed: ", wizard.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing else was touched", wizard.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// One wizard writing the runtime while another removes it leaves a tree neither of them
    /// describes. The live lock the journal already keeps is what recognises the other one.
    /// </summary>
    [Fact]
    public async Task A_second_wizard_is_refused_while_another_run_holds_its_lock()
    {
        using var other = InstallerRunJournal.Start(journalDirectory);

        var wizard = await Wizard();

        Assert.True(wizard.IsBlocked);
        Assert.Contains("Another LocalAi installer", wizard.BlockingNotice!, StringComparison.Ordinal);
        Assert.False(wizard.CanMoveNext);
        Assert.Empty(wizard.Repositories);
    }

    [Fact]
    public async Task A_finished_run_no_longer_blocks_the_next_wizard()
    {
        using (var previous = InstallerRunJournal.Start(journalDirectory))
        {
            previous.Finish(InstallerRunOutcome.Completed);
        }

        var wizard = await Wizard();

        Assert.False(wizard.IsBlocked);
        Assert.True(wizard.CanMoveNext);
    }

    [Fact]
    public async Task With_nothing_installed_the_wizard_says_so_instead_of_planning()
    {
        Directory.Delete(machine.Runtime, recursive: true);

        var wizard = await Wizard();

        Assert.True(wizard.IsBlocked);
        Assert.Contains("no LocalAi installation", wizard.BlockingNotice!, StringComparison.Ordinal);
        // The documentation that listed the leftovers was deleted; the notice cannot send anyone there.
        Assert.DoesNotContain("documentation", wizard.BlockingNotice!, StringComparison.Ordinal);
        Assert.False(wizard.CanMoveNext);
    }

    /// <summary>
    /// The removal half of a clean reinstall: it opens on the row that keeps what an hour of
    /// embedding built, and the page's own radio buttons agree with the boxes underneath them.
    /// </summary>
    [Fact]
    public async Task A_clean_reinstall_opens_on_the_reinstall_friendly_row()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);

        Assert.Equal(RemovalPreset.ReinstallFriendly, wizard.SelectedPreset);
        Assert.True(wizard.Presets
            .Single(preset => preset.Preset == RemovalPreset.ReinstallFriendly).IsSelected);
        Assert.False(wizard.Presets
            .Single(preset => preset.Preset == RemovalPreset.FullUninstall).IsSelected);
        Assert.True(Row(wizard, RemovalItem.Binaries).IsSelected);
        Assert.False(Row(wizard, RemovalItem.RepositoryIndexes).IsSelected);
    }

    /// <summary>
    /// The install half is offered when the removal half has finished and succeeded, never
    /// before and never after a failure: inviting somebody to install over a removal that
    /// stopped halfway is how a machine ends up in a state neither wizard describes.
    /// </summary>
    [Fact]
    public async Task The_install_half_is_offered_only_after_a_removal_that_worked()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        Assert.False(wizard.CanContinueToInstall);

        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;
        Assert.False(wizard.CanContinueToInstall);

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.CanContinueToInstall);
        Assert.Contains(
            "Continue to the installation to finish the reinstall.",
            wizard.StepDescription,
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(machine.Runtime, "repositories")));
    }

    /// <summary>
    /// The finish page used to say the kept things were waiting "either way", which read as
    /// "stopping here costs nothing". With the hook dispatchers kept it no longer is: every
    /// connected repository fails its hooks until a launcher is back. Close is still on the
    /// screen; the page just stops calling it equivalent.
    /// </summary>
    [Fact]
    public async Task Stopping_after_the_removal_is_not_called_free()
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("either way", wizard.StepDescription, StringComparison.Ordinal);
        Assert.EndsWith(
            " Until it finishes, the Git hooks in your connected repositories fail: they call " +
            "the launcher this run removed.",
            wizard.StepDescription,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// That consequence is stated only when the run caused it: not when the hooks row took the
    /// dispatchers as well, and not when the binaries row kept the launcher.
    /// </summary>
    [Theory]
    [InlineData(RemovalItem.GitHooks, true)]
    [InlineData(RemovalItem.Binaries, false)]
    public async Task The_finish_page_mentions_the_hooks_only_when_the_run_left_them_failing(
        RemovalItem item,
        bool selected)
    {
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        Row(wizard, item).IsSelected = selected;
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Everything selected was removed. Continue to the installation to finish the " +
            "reinstall.",
            wizard.StepDescription);
    }

    [Fact]
    public async Task An_ordinary_removal_never_offers_to_install_again()
    {
        var wizard = await Wizard();
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.False(wizard.OffersInstallAfterwards);
        Assert.False(wizard.CanContinueToInstall);
    }

    [Fact]
    public async Task A_removal_that_failed_does_not_invite_an_install_over_it()
    {
        var held = Path.Combine(machine.Runtime, "jobs", "job.json");
        using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        var wizard = await Wizard(
            preset: RemovalPreset.ReinstallFriendly,
            offersInstallAfterwards: true);
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;

        Assert.False(await wizard.RunAsync(TestContext.Current.CancellationToken));

        Assert.True(wizard.HasRunError);
        Assert.False(wizard.CanContinueToInstall);
    }

    /// <summary>
    /// The copy parked inside the installation cannot remove the tree it runs from. It hands
    /// over to a temporary copy before the wizard opens; when that failed, or when it was
    /// started by hand, the wizard refuses before anything is chosen and says what to do instead.
    /// </summary>
    [Fact]
    public async Task The_copy_inside_the_installation_refuses_and_says_why()
    {
        var inside = Path.Combine(machine.Layout.BinRoot, "uninstall", "LocalAi.Installer.exe");
        var wizard = new UninstallWizardViewModel(
            layout: machine.Layout,
            homeDirectory: machine.Home,
            processRunner: new StubProcessRunner(),
            readHooksPath: machine.HooksPathReader,
            executablePath: inside)
        {
            LogDirectory = journalDirectory,
        };

        await wizard.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(wizard.IsBlocked);
        Assert.False(wizard.CanMoveNext);
        Assert.False(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        Assert.Contains(inside, wizard.BlockingNotice, StringComparison.Ordinal);
        Assert.Contains(machine.Layout.Root, wizard.BlockingNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The review page promises that what it does not name is not touched, and a removal parks
    /// the previous version of every client file beside it — the skill it deletes included. So
    /// every file the run leaves in the home directory is named on the review page before it and
    /// in the report after it (#614).
    /// </summary>
    [Fact]
    public async Task Every_file_the_run_leaves_in_the_home_directory_was_named_before_and_after()
    {
        var skill = Path.Combine(machine.Home, ManagedInstructionBlock.ClaudeSkillRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, ManagedInstructionBlock.SkillFile());
        var wizard = await Wizard(preset: RemovalPreset.DisconnectClients);
        await wizard.MoveNextAsync(TestContext.Current.CancellationToken);
        wizard.IsConfirmed = true;
        var review = string.Join("\n", wizard.ReviewGroups.SelectMany(group => group.Lines));
        var before = RemovalFixture.Snapshot(machine.Home);

        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));

        var left = RemovalFixture.Snapshot(machine.Home).Keys.Except(before.Keys).ToArray();
        Assert.Contains(left, path => path.StartsWith(skill + ".", StringComparison.Ordinal));
        Assert.All(left, path =>
        {
            Assert.Contains(path, review, StringComparison.Ordinal);
            Assert.Contains(path, wizard.Report, StringComparison.Ordinal);
            Assert.Contains(wizard.Results, row => row.Title == path);
        });
    }

    private static RemovalRow Row(UninstallWizardViewModel wizard, RemovalItem item) =>
        wizard.Rows.Single(row => row.Item == item);

    private async Task<UninstallWizardViewModel> Wizard(
        IProcessRunner? processRunner = null,
        RemovalPreset preset = RemovalPreset.FullUninstall,
        bool offersInstallAfterwards = false)
    {
        var wizard = new UninstallWizardViewModel(
            preset,
            offersInstallAfterwards,
            machine.Layout,
            machine.Home,
            processRunner ?? new StubProcessRunner(),
            machine.HooksPathReader,
            // Never the machine's own PATH, which names the developer's own launcher.
            inspector: new ExistingLocalAiInspector(new SystemFileSystemProbe(), () => null))
        {
            LogDirectory = journalDirectory,
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        return wizard;
    }

    /// <summary>Stands in for the launcher, which is the only process this wizard starts.</summary>
    private sealed class StubProcessRunner : IProcessRunner
    {
        public ProcessResult Result { get; set; } =
            new(0, string.Empty, string.Empty, false, false);

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }
}
