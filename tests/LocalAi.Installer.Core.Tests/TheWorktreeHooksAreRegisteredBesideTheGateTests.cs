using System.Text.Json.Nodes;
using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Claude Code decides where a worktree goes, and the only way to change that decision is a
/// `WorktreeCreate` hook — the client has no setting for it. Until the installer writes those
/// two entries, a client opens worktrees inside the repository, where everything that walks the
/// tree reads them as part of it, and Codex opening the same branch lands somewhere else.
///
/// So they are registered whenever the Claude Code integration is configured at all, rather
/// than being a question of their own: the question "where do worktrees go" is answered per
/// repository by `git config localai.worktreeRoot`, not per machine by a checkbox.
///
/// Two entries, one per event, because an entry lives under one event key. One signature for
/// both, so that a line somebody copied from one event to the other is still recognised as ours
/// and still removed — the case Remove's "every entry of ours, not the first one found" was
/// written for.
/// </summary>
public sealed class TheWorktreeHooksAreRegisteredBesideTheGateTests
{
    private const string CreateCommand =
        "\"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" run localai agent worktree create";

    private const string RemoveCommand =
        "\"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" run localai agent worktree remove";

    private const string GateCommand =
        "\"C:\\LocalAi\\bin\\launcher\\localai-launcher.exe\" run localai agent gate";

    [Fact]
    public void Each_event_gets_an_entry_of_its_own()
    {
        var written = Register("{}");

        Assert.Equal(CreateCommand, Command(written, "WorktreeCreate"));
        Assert.Equal(RemoveCommand, Command(written, "WorktreeRemove"));
    }

    /// <summary>
    /// `PreToolUse` carries a matcher naming the tools it answers for. These two events have no
    /// tool to match on, and an empty matcher is not the same as none.
    /// </summary>
    [Fact]
    public void Neither_entry_carries_a_matcher()
    {
        var written = Register("{}");

        foreach (var name in new[] { "WorktreeCreate", "WorktreeRemove" })
        {
            Assert.Null(Group(written, name)!["matcher"]);
        }
    }

    /// <summary>
    /// The gate's ten seconds would kill this one mid-checkout: creating a worktree runs
    /// `git worktree add`, which writes out the whole tree, and the command allows two minutes
    /// for a single git call.
    /// </summary>
    [Fact]
    public void The_timeout_allows_for_a_checkout_rather_than_a_process_start()
    {
        var written = Register("{}");

        foreach (var name in new[] { "WorktreeCreate", "WorktreeRemove" })
        {
            Assert.Equal(180, Hook(written, name)!["timeout"]!.GetValue<int>());
        }
    }

    [Fact]
    public void Registering_twice_changes_nothing_the_second_time()
    {
        var once = Register("{}");

        var twice = Register(once);

        Assert.Equal(once, twice, StringComparer.Ordinal);
    }

    /// <summary>
    /// An installation that moved keeps one entry rather than gaining a second: the launcher
    /// path is part of the command, and ours is recognised by the command's tail.
    /// </summary>
    [Fact]
    public void An_entry_left_by_an_install_at_another_path_is_replaced_not_doubled()
    {
        var elsewhere = ClaudeHookRegistration.Add(
            "{}",
            ClaudeHook.WorktreeCreate,
            "\"D:\\Elsewhere\\localai-launcher.exe\" run localai agent worktree create");

        var written = Register(elsewhere);

        Assert.Single(Groups(written, "WorktreeCreate"));
        Assert.Equal(CreateCommand, Command(written, "WorktreeCreate"));
    }

    [Fact]
    public void Removing_them_takes_both_events_out()
    {
        var written = Register("{}");

        var removed = Unregister(written);

        Assert.Null(Hooks(removed));
    }

    /// <summary>
    /// The two live beside the gate in one file, and each must be removable without the other:
    /// an uninstall can take the worktree hooks while the person keeps their text-search refusal.
    /// </summary>
    [Fact]
    public void Removing_them_leaves_the_gate_alone()
    {
        var both = ClaudeHookRegistration.Add(Register("{}"), ClaudeHook.Gate, GateCommand);

        var removed = Unregister(both);

        Assert.Equal(GateCommand, Command(removed, "PreToolUse"));
        Assert.Null(Hooks(removed)!["WorktreeCreate"]);
    }

    [Fact]
    public void Removing_the_gate_leaves_them_alone()
    {
        var both = ClaudeHookRegistration.Add(Register("{}"), ClaudeHook.Gate, GateCommand);

        var removed = ClaudeHookRegistration.Remove(both, ClaudeHook.Gate);

        Assert.Equal(CreateCommand, Command(removed, "WorktreeCreate"));
        Assert.Null(Hooks(removed)!["PreToolUse"]);
    }

    /// <summary>
    /// Somebody else's entry on the same event stays exactly where it is, and keeps the
    /// container alive after ours is gone.
    /// </summary>
    [Fact]
    public void Somebody_elses_entry_on_the_same_event_survives()
    {
        var theirs = """
            {
              "hooks": {
                "WorktreeCreate": [
                  { "hooks": [ { "type": "command", "command": "audit --worktree" } ] }
                ]
              }
            }
            """;

        var removed = Unregister(Register(theirs));

        Assert.Contains("audit --worktree", removed, StringComparison.Ordinal);
        Assert.Single(Groups(removed, "WorktreeCreate"));
    }

    [Fact]
    public void Entries_that_are_not_ours_are_counted_for_the_notice()
    {
        var theirs = """
            {
              "hooks": {
                "WorktreeRemove": [
                  { "hooks": [ { "type": "command", "command": "audit --worktree" } ] }
                ]
              }
            }
            """;

        var written = Register(theirs);

        Assert.Equal(1, ClaudeHookRegistration.OtherEntries(written, ClaudeHook.WorktreeRemove));
        Assert.Equal(0, ClaudeHookRegistration.OtherEntries(written, ClaudeHook.WorktreeCreate));
    }

    /// <summary>
    /// Half a registration is worse than none: a client that creates worktrees beside the
    /// repository and never removes them leaves one behind at every session exit. So "is it
    /// registered" is true only when both events carry ours, and a run that finds one fixes it.
    /// </summary>
    [Fact]
    public void One_event_alone_does_not_count_as_registered()
    {
        var half = ClaudeHookRegistration.Add("{}", ClaudeHook.WorktreeCreate, CreateCommand);

        Assert.False(ClaudeHookRegistration.IsRegistered(half, ClaudeHook.WorktreeRemove));
        Assert.True(ClaudeHookRegistration.IsRegistered(half, ClaudeHook.WorktreeCreate));
    }

    // -- helpers ---------------------------------------------------------------

    private static string Register(string settings)
    {
        var text = ClaudeHookRegistration.Add(settings, ClaudeHook.WorktreeCreate, CreateCommand);
        return ClaudeHookRegistration.Add(text, ClaudeHook.WorktreeRemove, RemoveCommand);
    }

    private static string Unregister(string settings)
    {
        var text = ClaudeHookRegistration.Remove(settings, ClaudeHook.WorktreeCreate);
        return ClaudeHookRegistration.Remove(text, ClaudeHook.WorktreeRemove);
    }

    private static JsonObject? Hooks(string settings) =>
        JsonNode.Parse(settings)?["hooks"] as JsonObject;

    private static JsonArray Groups(string settings, string name) =>
        (JsonArray)Hooks(settings)![name]!;

    private static JsonObject? Group(string settings, string name) =>
        Groups(settings, name)[0] as JsonObject;

    private static JsonObject? Hook(string settings, string name) =>
        (Group(settings, name)!["hooks"] as JsonArray)![0] as JsonObject;

    private static string? Command(string settings, string name) =>
        Hook(settings, name)!["command"]!.GetValue<string>();
}
