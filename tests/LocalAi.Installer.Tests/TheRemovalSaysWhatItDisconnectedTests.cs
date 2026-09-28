using System.Text;
using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Abstractions;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.Core.Removal;
using LocalAi.Installer.Core.Transactions;
using LocalAi.Installer.ViewModels;
using LocalAi.TestFixtures;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The summary names the client plans that went through, by the headings the review page gave
/// them, instead of counting their files; and a client file the run deleted is reported as
/// removed rather than updated (#614).
/// </summary>
public sealed class TheRemovalSaysWhatItDisconnectedTests : IDisposable
{
    private readonly RemovalFixture machine = new();

    public void Dispose() => machine.Dispose();

    /// <summary>
    /// Disconnecting removes nothing from the runtime, and the summary used to open with
    /// "Paths removed from {root}: 0." (RC31 sandbox run 20260928-144300-remove-presets): what was
    /// disconnected comes first, the hook dispatchers next, and the runtime is said to be
    /// untouched last.
    /// </summary>
    [Theory]
    [InlineData(InstallerLanguage.English,
        "Disconnected: Claude integration, Codex integration, Claude Code hooks. Git hook dispatchers removed: ",
        " Nothing in {0} was removed.")]
    [InlineData(InstallerLanguage.Russian,
        "Отключено: Интеграция с Claude, Интеграция с Codex, Хуки Claude Code. Удалено диспетчеров хуков Git: ",
        " В {0} ничего не удалено.")]
    public async Task The_summary_names_what_was_disconnected_in_the_review_pages_order(
        InstallerLanguage language, string opening, string closing)
    {
        await ConnectSkillsAndHooks();
        var wizard = await Run(RemovalPreset.DisconnectClients);

        wizard.Presentation.ChooseLanguage(language);

        Assert.StartsWith(opening, wizard.Summary, StringComparison.Ordinal);
        Assert.EndsWith(string.Format(closing, machine.Layout.Root), wizard.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reinstall-friendly preset keeps the Claude row and still takes the hooks with the
    /// launcher, so "Claude integration" here would name something that was not disconnected.
    /// </summary>
    [Fact]
    public async Task The_hooks_are_named_as_hooks_and_never_as_the_claude_integration()
    {
        await ConnectSkillsAndHooks();
        var wizard = await Run(RemovalPreset.ReinstallFriendly);

        Assert.EndsWith(" Disconnected: Claude Code hooks.", wizard.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InstallerLanguage.English, "Updated {0}, {1}; removed {2} — LocalAi disconnected")]
    [InlineData(InstallerLanguage.Russian, "Обновлено {0}, {1}; удалено {2} — LocalAi отключён")]
    public async Task The_report_file_says_which_client_files_were_removed(
        InstallerLanguage language, string line)
    {
        await ConnectSkillsAndHooks();
        var entries = await Disconnect(language);

        Assert.Contains(
            string.Format(line, ClaudeJson, ClaudeInstructions, ClaudeSkill),
            entries.Select(RemovalReportPresentation.Line));
    }

    [Theory]
    [InlineData(InstallerLanguage.English, "Removed {0} — LocalAi disconnected")]
    [InlineData(InstallerLanguage.Russian, "Удалено {0} — LocalAi отключён")]
    public async Task A_plan_that_only_removed_a_file_does_not_say_updated(
        InstallerLanguage language, string line)
    {
        await ConnectSkillsAndHooks();
        // Nothing of ours is left in either file, so the skill is all the Claude plan takes.
        File.WriteAllText(ClaudeJson, "{\"theme\": \"dark\"}\n", new UTF8Encoding(false));
        File.WriteAllText(ClaudeInstructions, RemovalFixture.ClaudeInstructionsPreamble, new UTF8Encoding(false));
        var entries = await Disconnect(language);

        Assert.Contains(string.Format(line, ClaudeSkill), entries.Select(RemovalReportPresentation.Line));
    }

    /// <summary>
    /// The title lists exactly the files that went, so the line under it names none of them, and
    /// nothing was rewritten for it to say "Updated".
    /// </summary>
    [Theory]
    [InlineData(InstallerLanguage.English, "Removed — LocalAi disconnected")]
    [InlineData(InstallerLanguage.Russian, "Удалено — LocalAi отключён")]
    public async Task The_finish_page_says_removed_when_the_plan_only_removed_files(
        InstallerLanguage language, string detail)
    {
        await ConnectSkillsAndHooks();
        File.WriteAllText(ClaudeJson, "{\"theme\": \"dark\"}\n", new UTF8Encoding(false));
        File.WriteAllText(ClaudeInstructions, RemovalFixture.ClaudeInstructionsPreamble, new UTF8Encoding(false));
        var rows = (await Disconnect(language)).Select(RemovalReportPresentation.Row).ToArray();

        Assert.Contains(new RunResultRow(RunMark.Done, ClaudeSkill, detail), rows);
    }

    /// <summary>
    /// The title already carries the full paths, so the line under it names only the file that
    /// went. Its backup is still reported as the previous version.
    /// </summary>
    [Theory]
    [InlineData(InstallerLanguage.English, "Updated; removed SKILL.md — LocalAi disconnected",
        "Kept — the previous version of {0}")]
    [InlineData(InstallerLanguage.Russian, "Обновлено; удалено SKILL.md — LocalAi отключён",
        "Оставлено — прежняя версия {0}")]
    public async Task The_finish_page_names_the_removed_file_under_the_full_paths(
        InstallerLanguage language, string detail, string backup)
    {
        await ConnectSkillsAndHooks();
        var rows = (await Disconnect(language)).Select(RemovalReportPresentation.Row).ToArray();

        Assert.Contains(
            new RunResultRow(RunMark.Done, ClaudeJson + ", " + ClaudeInstructions + ", " + ClaudeSkill, detail),
            rows);
        Assert.Contains(rows, row =>
            row.Title.StartsWith(ClaudeSkill + ".", StringComparison.Ordinal) &&
            row.Detail == string.Format(backup, ClaudeSkill));
    }

    private string ClaudeJson => Path.Combine(machine.Home, ".claude.json");

    private string ClaudeInstructions => Path.Combine(machine.Home, ".claude", "CLAUDE.md");

    private string ClaudeSkill => Path.Combine(machine.Home, ManagedInstructionBlock.ClaudeSkillRelativePath);

    /// <summary>The real runner over a disconnect, which starts no process and reads no PATH or registry.</summary>
    private async Task<IReadOnlyList<RemovalReportEntry>> Disconnect(InstallerLanguage language)
    {
        var plan = await machine.PlanAsync(
            RemovalSelection.FromPreset(RemovalPreset.DisconnectClients),
            TestContext.Current.CancellationToken);
        using var journal = InstallerRunJournal.Start(Path.Combine(machine.Root, "journal"));
        var outcome = await new UninstallRunner(machine.Layout, new Exits())
            .ApplyAsync(plan, journal, TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);
        InstallerCulture.Current = language;
        return UninstallReport.Capture(plan, journal.Snapshot, machine.Layout);
    }

    private async Task<UninstallWizardViewModel> Run(RemovalPreset preset)
    {
        var wizard = new UninstallWizardViewModel(
            preset,
            layout: machine.Layout,
            homeDirectory: machine.Home,
            processRunner: new Exits(),
            readHooksPath: machine.HooksPathReader,
            inspector: new ExistingLocalAiInspector(new SystemFileSystemProbe(), () => null))
        {
            LogDirectory = Path.Combine(machine.Root, "reports"),
        };
        await wizard.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await wizard.MoveNextAsync(TestContext.Current.CancellationToken));
        wizard.IsConfirmed = true;
        Assert.True(await wizard.RunAsync(TestContext.Current.CancellationToken));
        return wizard;
    }

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

    private sealed class Exits : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, "", "", false, false));
    }
}
