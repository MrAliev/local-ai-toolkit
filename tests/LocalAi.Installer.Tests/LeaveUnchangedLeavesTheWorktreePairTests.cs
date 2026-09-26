using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// "Leave unchanged" on Claude Code's row is a promise about that client's settings file. The
/// worktree pair used to follow the row alone, so a machine that already had it lost it on a run
/// that was told to leave the client as it was.
/// </summary>
public sealed class LeaveUnchangedLeavesTheWorktreePairTests
{
    private const string Settings = @"C:\Users\Someone\.claude\settings.json";

    [Fact]
    public void Leaving_the_client_alone_keeps_a_pair_that_is_already_there()
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyHookState(gateRegistered: false, worktreeRegistered: true, Settings);

        page.SetChoice("claude", AgentChoice.NoChange);

        Assert.True(page.RegistersWorktrees);
        Assert.False(page.WorktreeChanges);
        Assert.False(page.SettingsChanges);
        Assert.StartsWith(
            "Worktrees in Claude Code: created in a worktrees folder beside the repository " +
            "instead of inside it. Recorded in",
            page.WorktreeReviewText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_the_client_alone_adds_no_pair_that_is_not_there()
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyHookState(gateRegistered: false, worktreeRegistered: false, Settings);

        page.SetChoice("claude", AgentChoice.NoChange);

        Assert.False(page.RegistersWorktrees);
        Assert.False(page.SettingsChanges);
        Assert.StartsWith(
            "Worktrees in Claude Code: left where Claude Code puts them.",
            page.WorktreeReviewText,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuring_the_client_registers_the_pair(bool alreadyThere)
    {
        var page = new AgentIntegrationPageViewModel();
        page.ApplyHookState(gateRegistered: false, worktreeRegistered: alreadyThere, Settings);

        page.SetChoice("claude", AgentChoice.InstructionsOnly);

        Assert.True(page.RegistersWorktrees);
        Assert.Equal(!alreadyThere, page.WorktreeChanges);
    }
}
