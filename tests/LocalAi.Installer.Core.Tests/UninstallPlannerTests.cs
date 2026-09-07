using System.Text;
using LocalAi.TestFixtures;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Removal;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The plan over a populated machine: what each preset would take, said before anything is
/// taken. The machine itself is <see cref="RemovalFixture"/>.
/// </summary>
public sealed class UninstallPlannerTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task A_full_uninstall_names_every_removal()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        Assert.True(plan.HasWork);
        foreach (var name in Directory.EnumerateFileSystemEntries(machine.Runtime)
                     .Select(Path.GetFileName)
                     .Where(name => name != RemovalMatrix.SigningKeyDirectoryName))
        {
            var path = Path.Combine(machine.Runtime, name!);
            Assert.Contains(plan.Paths, entry => entry.Path == path);
            Assert.Contains(path, plan.PreviewText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The loudest line on the page. A key directory removed by a preset, rather than by the
    /// person saying so a second time, is an offline backup silently promoted to sole copy.
    /// </summary>
    [Fact]
    public async Task The_signing_keys_stay_unless_they_are_separately_confirmed()
    {
        var keys = Path.Combine(machine.Runtime, RemovalMatrix.SigningKeyDirectoryName);

        var kept = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var removed = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true));

        Assert.DoesNotContain(kept.Paths, entry => entry.Path == keys);
        Assert.Contains(
            kept.Retained,
            notice => notice.Detail.Contains(keys, StringComparison.Ordinal));
        Assert.Contains(removed.Paths, entry => entry.Path == keys);
    }

    /// <summary>
    /// The default full uninstall keeps the signing keys, so the root outlives it — and the
    /// plan says so from what is on disk rather than from which boxes were ticked. Confirming
    /// the keys is what empties it.
    /// </summary>
    [Fact]
    public async Task Whether_the_root_survives_is_read_from_what_is_left_in_it()
    {
        var keys = Path.Combine(machine.Runtime, RemovalMatrix.SigningKeyDirectoryName);

        var withKeys = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));
        var withoutKeys = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithSigningKeyRemoval(true));

        Assert.False(withKeys.RemovesRuntimeRootEntirely);
        Assert.Equal([keys], withKeys.RetainedPaths);
        Assert.Contains("keep " + keys, withKeys.PreviewText, StringComparison.Ordinal);
        Assert.True(withoutKeys.RemovesRuntimeRootEntirely);
        Assert.Empty(withoutKeys.RetainedPaths);
    }

    [Fact]
    public async Task A_reinstall_friendly_run_leaves_the_indexes_and_settings_alone()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly));

        var removed = plan.Paths.Select(entry => entry.Path).ToArray();
        Assert.Contains(Path.Combine(machine.Runtime, "bin"), removed);
        Assert.Contains(Path.Combine(machine.Runtime, "jobs"), removed);
        Assert.DoesNotContain(Path.Combine(machine.Runtime, "repositories"), removed);
        Assert.False(plan.RemovesRuntimeRootEntirely);
        Assert.All(
            RemovalMatrix.SettingsFileNames,
            name => Assert.DoesNotContain(Path.Combine(machine.Runtime, name), removed));
    }

    [Fact]
    public async Task Disconnecting_clients_touches_no_runtime_path()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.Empty(plan.Paths);
        Assert.True(plan.HasWork);
        // Three plans for two clients: the text-search gate is planned, applied and failed
        // apart from the client that carries it, because the file it edits is one the person
        // edits by hand and the client's is not (#423).
        Assert.Equal(
            ["Claude", "Claude text-search gate", "Codex"],
            plan.AgentConfigurations.Select(agent => agent.AgentName));
    }

    /// <summary>
    /// The runtime root is shared, and a later release will put something here this installer
    /// has never heard of. A full uninstall that quietly stepped around it would leave the
    /// machine dirty while reporting success.
    /// </summary>
    [Fact]
    public async Task Unrecognised_runtime_files_are_listed_rather_than_left_behind()
    {
        var stranger = Path.Combine(machine.Runtime, "something-a-later-release-added.json");

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var entry = Assert.Single(plan.Paths, path => path.Path == stranger);
        Assert.Equal(RemovalItem.OtherRuntimeFiles, entry.Item);
        Assert.Contains(stranger, plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_selected_plans_nothing()
    {
        var plan = await Plan(RemovalSelection.Nothing());

        Assert.False(plan.HasWork);
        Assert.Empty(plan.Paths);
        Assert.Empty(plan.Hooks);
        Assert.Contains("would change nothing", plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_clients_lose_their_registrations_and_their_managed_block()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var claudeJson = RemovalFixture.PlannedFile(plan, "Claude", ".claude.json");
        var codexToml = RemovalFixture.PlannedFile(plan, "Codex", "config.toml");

        Assert.DoesNotContain("codesearch", claudeJson.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("locallm", claudeJson.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.codesearch", codexToml.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_servers.locallm", codexToml.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ManagedInstructionBlock.BeginMarker,
            RemovalFixture.PlannedFile(plan, "Claude", "CLAUDE.md").AfterText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ManagedInstructionBlock.BeginMarker,
            RemovalFixture.PlannedFile(plan, "Codex", "AGENTS.md").AfterText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The promise the managed markers exist to make, checked from the other direction: an
    /// uninstall gives the file back exactly as the person had it.
    /// </summary>
    [Fact]
    public async Task What_the_user_wrote_outside_the_markers_survives_byte_for_byte()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.Equal(
            RemovalFixture.ClaudeInstructionsPreamble,
            RemovalFixture.PlannedFile(plan, "Claude", "CLAUDE.md").AfterText);
        Assert.Equal(
            RemovalFixture.CodexInstructionsPreamble,
            RemovalFixture.PlannedFile(plan, "Codex", "AGENTS.md").AfterText);
    }

    [Fact]
    public async Task Another_clients_registrations_and_settings_are_not_ours_to_remove()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var claudeJson = RemovalFixture.PlannedFile(plan, "Claude", ".claude.json").AfterText;
        var codexToml = RemovalFixture.PlannedFile(plan, "Codex", "config.toml").AfterText;

        Assert.Contains("\"someone-elses-server\"", claudeJson, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"dark\"", claudeJson, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", codexToml, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-5\"", codexToml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hooks_are_listed_from_the_manifests_with_their_chained_originals()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "plain");
        Assert.Equal(machine.PlainHooks, hook.HooksDirectory);
        Assert.Equal(
            [
                Path.Combine(machine.PlainHooks, "post-commit"),
                Path.Combine(machine.PlainHooks, "post-merge"),
            ],
            hook.Dispatchers);
        Assert.Equal(
            [Path.Combine(machine.PlainHooks, "post-commit.pre-localai")],
            hook.RestoredHooks);
        Assert.Empty(hook.ExcludePatterns);
    }

    /// <summary>
    /// A hook the person wrote themselves carries no marker. Installation chains it rather than
    /// replacing it, and removal has to leave it exactly where it is.
    /// </summary>
    [Fact]
    public async Task A_hook_that_is_not_ours_is_left_alone()
    {
        var foreign = Path.Combine(machine.PlainHooks, "post-checkout");

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "plain");
        Assert.DoesNotContain(foreign, hook.Dispatchers);
        Assert.DoesNotContain(foreign, plan.PreviewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// husky points core.hooksPath at a directory it rewrites on every npm install, so the
    /// dispatchers live in its parent. Removal has to look where installation actually wrote
    /// them, or it reports success over hooks that are still there.
    /// </summary>
    [Fact]
    public async Task Hooks_are_found_where_core_hooksPath_sends_them()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "husky");
        Assert.Equal(machine.HuskyHooks, hook.HooksDirectory);
        Assert.Equal([Path.Combine(machine.HuskyHooks, "post-commit")], hook.Dispatchers);
    }

    /// <summary>
    /// A hooks directory inside the working tree made installation write ignore rules into
    /// .git/info/exclude — one for the dispatcher and one for the chained original it parks
    /// beside it. Both were ours to write and both are ours to take back, and the header goes
    /// with them because it introduces nothing once they are gone.
    /// </summary>
    [Fact]
    public async Task The_exclude_lines_installation_wrote_are_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "husky");
        Assert.Equal(machine.HuskyExclude, hook.ExcludePath);
        Assert.Equal(
            [
                "# LocalAi managed Git hooks",
                "/.husky/post-commit",
                "/.husky/post-commit.pre-localai",
            ],
            hook.ExcludePatterns);
        Assert.Contains("/.husky/post-commit", plan.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_that_no_longer_exists_is_skipped_and_named()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        var hook = Assert.Single(plan.Hooks, entry => entry.RepositoryId == "gone");
        Assert.True(hook.IsSkipped);
        Assert.False(hook.HasWork);
        Assert.Contains("no longer exists", hook.SkipReason!, StringComparison.Ordinal);
        Assert.Contains(hook.CommonDirectory, plan.PreviewText, StringComparison.Ordinal);
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(hook.CommonDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hook_removal_can_be_narrowed_to_chosen_repositories()
    {
        var plan = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .WithRepositories(["husky"]));

        Assert.Equal(["husky"], plan.Hooks.Select(hook => hook.RepositoryId));
    }

    /// <summary>
    /// The one combination the matrix allows that leaves something visibly broken: hooks that
    /// call a launcher which is no longer installed. Saying so is cheap; finding out at the
    /// next commit is not.
    /// </summary>
    [Fact]
    public async Task Keeping_the_hooks_while_removing_the_binaries_is_called_out()
    {
        var plan = await Plan(RemovalSelection
            .FromPreset(RemovalPreset.FullUninstall)
            .With(RemovalItem.GitHooks, false));

        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(
                "launcher that is no longer there",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task What_is_never_touched_is_stated_on_every_run()
    {
        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.FullUninstall));

        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains("winget uninstall", StringComparison.Ordinal));
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains("ollama rm", StringComparison.Ordinal));
        Assert.Contains(
            plan.Retained,
            notice => notice.Detail.Contains(
                RemovalMatrix.JournalDirectoryName,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A launcher removed with the dispatchers left in place breaks every connected
    /// repository. Whether that is a state or an interval depends on something the planner
    /// cannot see — so it is told, and the two answers say different things.
    /// </summary>
    [Fact]
    public async Task The_broken_hooks_are_an_interval_when_an_installation_follows()
    {
        var selection = RemovalSelection.FromPreset(RemovalPreset.ReinstallFriendly);

        var alone = await Plan(selection);
        var beforeAnInstall = await machine.PlanAsync(
            selection,
            TestContext.Current.CancellationToken,
            installationFollows: true);

        var stated = Assert.Single(
            alone.Retained,
            notice => notice.Title == "Git hook dispatchers");
        Assert.Contains("from then on", stated.Detail, StringComparison.Ordinal);
        Assert.Contains("remove them here or by hand", stated.Detail, StringComparison.Ordinal);

        var bounded = Assert.Single(
            beforeAnInstall.Retained,
            notice => notice.Title == "Git hook dispatchers");
        Assert.Contains(
            "until the installation that follows puts it back",
            bounded.Detail,
            StringComparison.Ordinal);
        // Advice to do destructive work for a reason that no longer holds.
        Assert.DoesNotContain("remove them here", bounded.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Disconnecting Claude takes the text-search gate with it, and as a plan of its own.
    ///
    /// Leaving it behind is the worst of the outcomes available here: a machine with no LocalAi
    /// on it that still turns down `grep`, pointing at a launcher that is gone, with nothing
    /// installed to explain why. Its own plan rather than the client's because the two files
    /// fail independently — `settings.json` is hand-edited, `.claude.json` is not (#423).
    /// </summary>
    [Fact]
    public async Task Disconnecting_claude_takes_the_text_search_gate_out()
    {
        const string before = "{\n  \"theme\": \"dark\"\n}";
        var settings = Path.Combine(machine.Home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(
            settings,
            ClaudeHookRegistration.Add(
                before,
                "\"C:\\Anywhere\\localai-launcher.exe\" run localai agent gate"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var gate = Assert.Single(
            plan.AgentConfigurations,
            configuration => configuration.Files.Any(file => file.Path == settings));
        Assert.Equal(before, Assert.Single(gate.Files).AfterText, StringComparer.Ordinal);
    }

    /// <summary>
    /// Somebody else's hook on the same event outlives ours, and the plan says so.
    ///
    /// The load-bearing sentence is the last one. A person who removes LocalAi and is then still
    /// refused a tool call concludes that the uninstall failed and goes looking for what it left
    /// behind — when the answer is that another entry, which was never ours to take, is doing it.
    /// </summary>
    [Fact]
    public async Task Other_hooks_left_on_the_same_event_are_named_with_what_they_will_do()
    {
        await WriteSettingsAsync(
            """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [ { "type": "command", "command": "audit --log" } ]
                  }
                ]
              }
            }
            """);

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        var notice = Assert.Single(
            plan.Retained,
            entry => entry.Title.Contains("PreToolUse", StringComparison.Ordinal));
        Assert.Contains(": 1.", notice.Detail, StringComparison.Ordinal);
        Assert.Contains(
            Path.Combine(machine.Home, ".claude", "settings.json"),
            notice.Detail,
            StringComparison.Ordinal);
        Assert.Contains("one of them is the reason", notice.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is not offered when there is nothing to explain. A notice about hooks this run did
    /// not touch, on a machine where ours was the only one, is a page of reassurance about a
    /// problem the reader does not have.
    /// </summary>
    [Fact]
    public async Task Nothing_else_on_that_event_means_no_notice_about_it()
    {
        await WriteSettingsAsync("{}");

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.DoesNotContain(
            plan.Retained,
            entry => entry.Title.Contains("PreToolUse", StringComparison.Ordinal));
    }

    /// <summary>
    /// Somebody else's hook on that event, and none of ours: nothing of ours is going, so nothing
    /// on that event changes and there is nothing to explain. A notice here would be a paragraph
    /// of reassurance about hooks this run never came near.
    ///
    /// The guard that decides this was uncovered — deleting it left the whole suite green — and
    /// with no settings file at all both branches answer null anyway, so the file has to exist and
    /// has to carry a stranger's entry for the test to reach the question.
    /// </summary>
    [Fact]
    public async Task Hooks_that_are_not_ours_alone_are_not_announced()
    {
        var settings = Path.Combine(machine.Home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(
            settings,
            """
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "audit --log" } ] }
                ]
              }
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);

        var plan = await Plan(RemovalSelection.FromPreset(RemovalPreset.DisconnectClients));

        Assert.DoesNotContain(
            plan.Retained,
            entry => entry.Title.Contains("PreToolUse", StringComparison.Ordinal));
    }

    /// <summary>
    /// And a run that is not disconnecting Claude says nothing about that client's hooks either,
    /// however many of them are there. The notice explains an effect this run is having; a run
    /// that leaves the file alone is not having it.
    /// </summary>
    [Fact]
    public async Task A_run_that_leaves_claude_alone_says_nothing_about_its_hooks()
    {
        await WriteSettingsAsync(
            """
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "audit --log" } ] }
                ]
              }
            }
            """);

        var plan = await Plan(RemovalSelection.Nothing());

        Assert.DoesNotContain(
            plan.Retained,
            entry => entry.Title.Contains("PreToolUse", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes a Claude settings file carrying our gate on top of whatever the caller supplies,
    /// which is how every one of these arrives on a real machine.
    /// </summary>
    private async Task WriteSettingsAsync(string existing)
    {
        var settings = Path.Combine(machine.Home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(
            settings,
            ClaudeHookRegistration.Add(
                existing,
                "\"C:\\Anywhere\\localai-launcher.exe\" run localai agent gate"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
    }

    private Task<UninstallPlan> Plan(RemovalSelection selection) =>
        machine.PlanAsync(selection, TestContext.Current.CancellationToken);
}
