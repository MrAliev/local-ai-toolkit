using System.Text;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Every client file a removal rewrites or deletes is first parked beside itself as
/// <c>&lt;file&gt;.&lt;time&gt;.bak</c>, and a deleted file's whole content stays there. The review
/// page promises that what it does not name is not touched, so it names each of them, and the
/// report names each one the run actually left (#614).
/// </summary>
public sealed class TheRemovalNamesTheBackupsItLeavesTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    [Fact]
    public async Task The_preview_names_the_backup_of_every_client_file_it_changes()
    {
        await ConnectSkillsAndHooks();

        var plan = await Plan();

        var files = plan.AgentConfigurations.SelectMany(configuration => configuration.Files).ToArray();
        Assert.Contains(files, file => file.Deletes);
        Assert.Contains(files, file => file.Path == SettingsPath);
        Assert.All(files, file => Assert.Contains(file.BackupPath, plan.PreviewText, StringComparison.Ordinal));
    }

    [Fact]
    public void The_preview_says_what_the_backup_holds()
    {
        Assert.Contains(
            "\n    keep a copy of the previous version: C:\\home\\.claude\\CLAUDE.md.20260901-100000.bak",
            "\n" + SyntheticPlan().PreviewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_says_what_the_backup_holds_in_russian()
    {
        InstallerCulture.Current = InstallerLanguage.Russian;

        Assert.Contains(
            "\n    оставить копию прежней версии: C:\\home\\.claude\\CLAUDE.md.20260901-100000.bak",
            "\n" + SyntheticPlan().PreviewText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A client step that fails halfway is rolled back from the backups it had made, and those
    /// stay; the files it never reached get none. So the report names what is on disk after the
    /// run — every new file, and nothing that is not there.
    /// </summary>
    [Fact]
    public async Task The_report_names_every_backup_the_run_left_and_no_other()
    {
        await ConnectSkillsAndHooks();
        var plan = await Plan();
        // Edited after the review page was built: the Codex step backs up config.toml, refuses
        // AGENTS.md, rolls config.toml back and never reaches the skill.
        File.AppendAllText(Path.Combine(machine.Home, ".codex", "AGENTS.md"), "An edit.\n");
        var before = RemovalFixture.Snapshot(machine.Home);
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));

        var outcome = await new UninstallRunner(machine.Layout, new NoProcess())
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        var left = RemovalFixture.Snapshot(machine.Home).Keys.Except(before.Keys).ToArray();
        Assert.Contains(Path.Combine(machine.Home, ".codex", "config.toml"), left.Select(StripBackupSuffix));
        Assert.Contains(Path.Combine(machine.Home, ManagedInstructionBlock.ClaudeSkillRelativePath), left.Select(StripBackupSuffix));
        var report = UninstallReport.Capture(plan, journal.Snapshot, machine.Layout);
        var named = report
            .Where(entry => entry.Target.EndsWith(".bak", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(left.Order(StringComparer.Ordinal), named.Select(entry => entry.Target).Order(StringComparer.Ordinal));
        Assert.All(named, entry =>
        {
            Assert.Equal(RemovalReportState.Retained, entry.State);
            Assert.Equal("the previous version of " + StripBackupSuffix(entry.Target), entry.Detail);
        });
    }

    [Fact]
    public async Task The_report_says_what_each_backup_is_in_russian()
    {
        await ConnectSkillsAndHooks();
        var plan = await Plan();
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        await new UninstallRunner(machine.Layout, new NoProcess())
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);
        InstallerCulture.Current = InstallerLanguage.Russian;

        var report = UninstallReport.Capture(plan, journal.Snapshot, machine.Layout);

        var skill = Path.Combine(machine.Home, ManagedInstructionBlock.ClaudeSkillRelativePath);
        var entry = Assert.Single(report, entry => StripBackupSuffix(entry.Target) == skill &&
            entry.Target != skill);
        Assert.Equal("прежняя версия " + skill, entry.Detail);
    }

    private string SettingsPath => Path.Combine(machine.Home, ".claude", "settings.json");

    private Task<UninstallPlan> Plan() =>
        machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            TestContext.Current.CancellationToken);

    /// <summary>The two skills and the settings hooks the fixture's clients do not carry.</summary>
    private async Task ConnectSkillsAndHooks()
    {
        foreach (var relative in new[]
                 {
                     ManagedInstructionBlock.ClaudeSkillRelativePath,
                     ManagedInstructionBlock.CodexSkillRelativePath,
                 })
        {
            var path = Path.Combine(machine.Home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ManagedInstructionBlock.SkillFile(), new UTF8Encoding(false));
        }

        var claude = new ClaudeConfigurationAdapter(machine.Home, machine.Layout.LauncherDirectory);
        await claude.ApplyAsync(
            claude.PreviewSettings(gate: true, worktree: true),
            TestContext.Current.CancellationToken);
    }

    private static string StripBackupSuffix(string path) =>
        path.EndsWith(".bak", StringComparison.Ordinal)
            ? path[..path.LastIndexOf('.', path.Length - ".bak".Length - 1)]
            : path;

    private static UninstallPlan SyntheticPlan()
    {
        var file = AgentConfigurationFileOperations.FilePlan(
            "C:\\home\\.claude\\CLAUDE.md",
            Encoding.UTF8.GetBytes("Mine.\n" + ManagedInstructionBlock.Block + "\n"),
            "Mine.\n",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        return new UninstallPlan(
            RuntimeRoot: "C:\\LocalAi",
            Selection: RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            Paths: [],
            RetainedPaths: [],
            AgentConfigurations: [new AgentConfigurationPlan("Claude", [file], string.Empty)],
            Hooks: [],
            Retained: []);
    }

    private sealed class NoProcess : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Disconnecting clients starts no process.");
    }
}
