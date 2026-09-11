using LocalAi.Installer.Core;
using LocalAi.Installer.Core.Diagnosis;
using LocalAi.Installer.ViewModels;

namespace LocalAi.Installer.Tests;

/// <summary>
/// The checkbox that refuses text searches in Claude Code, and the one property it has to hold:
/// it shows what this run will do, at every moment, without the installer remembering anything.
///
/// That is what makes a decline survive an update. Nothing records "they said no" — the box is
/// seeded by reading the machine, so an update proposes exactly what is already there, and a
/// person who took the entry out by hand is shown the truth rather than a memory of their answer
/// (#423).
/// </summary>
public sealed class TheGateBoxShowsWhatTheRunWillDoTests
{
    private const string Settings = @"C:\Users\Someone\.claude\settings.json";

    /// <summary>
    /// Default off, and it is not the same rule as the one above it. A detected client defaults to
    /// full integration because that is what makes the product work; this one refuses the person
    /// something, and a refusal that arrives pre-agreed is not agreed to.
    /// </summary>
    [Fact]
    public void Nothing_on_disk_leaves_the_box_unticked()
    {
        var page = Page();

        page.ApplyHookState(gateRegistered: false, worktreeRegistered: false, Settings);

        Assert.False(page.RefusesTextSearch);
    }

    [Fact]
    public void An_entry_on_disk_ticks_the_box()
    {
        var page = Page();

        page.ApplyHookState(gateRegistered: true, worktreeRegistered: false, Settings);

        Assert.True(page.RefusesTextSearch);
    }

    /// <summary>
    /// The refusal text sends the reader to the codesearch tools, so it is meaningless unless the
    /// servers are registered — and that choice is the combo directly above this box.
    /// </summary>
    [Theory]
    [InlineData(AgentChoice.McpAndInstructions, true)]
    [InlineData(AgentChoice.McpOnly, true)]
    [InlineData(AgentChoice.InstructionsOnly, false)]
    [InlineData(AgentChoice.NoChange, false)]
    public void The_box_is_offered_only_when_the_servers_are_being_registered(
        AgentChoice choice,
        bool offered)
    {
        var page = Page();

        page.SetChoice("claude", choice);

        Assert.Equal(offered, page.CanRefuseTextSearch);
    }

    /// <summary>
    /// Codex has no gate of its own, so its combo cannot enable or disable this one. Worth pinning
    /// because both clients live in the same collection and a loop over it would answer "yes" to
    /// whichever row was looked at last.
    /// </summary>
    [Fact]
    public void The_other_client_choice_does_not_decide_this()
    {
        var page = Page();
        page.SetChoice("claude", AgentChoice.NoChange);

        page.SetChoice("codex", AgentChoice.McpAndInstructions);

        Assert.False(page.CanRefuseTextSearch);
    }

    /// <summary>
    /// The rule for the disabled state, in both directions: it holds whatever the machine has, so
    /// the run writes nothing. Turning the servers off must not quietly withdraw a refusal that is
    /// already registered, and it must not quietly install one either.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Withdrawing_the_servers_puts_the_box_back_to_what_the_machine_has(bool registered)
    {
        var page = Page();
        page.ApplyHookState(registered, worktreeRegistered: false, Settings);
        page.SetChoice("claude", AgentChoice.McpAndInstructions);
        page.RefusesTextSearch = !registered;

        page.SetChoice("claude", AgentChoice.InstructionsOnly);

        Assert.Equal(registered, page.RefusesTextSearch);
    }

    /// <summary>
    /// Detection runs more than once in a session, and it must not undo a tick the person made
    /// between two runs of it — the same rule the combo above already follows.
    /// </summary>
    [Fact]
    public void Detecting_again_keeps_a_tick_the_person_made()
    {
        var page = Page();
        page.ApplyHookState(gateRegistered: false, worktreeRegistered: false, Settings);
        page.SetChoice("claude", AgentChoice.McpAndInstructions);
        page.RefusesTextSearch = true;

        page.ApplyDetection([Detected(AgentKind.Claude)]);

        Assert.True(page.RefusesTextSearch);
    }

    /// <summary>
    /// Four sentences rather than one with optional clauses. Naming an effect and naming a
    /// reversal are not the same sentence, and squeezing both through one hole is how "what will
    /// be configured" came to read as "what is being replaced" (#256).
    ///
    /// The two reversal cases are the only guard against a silent change in either direction, so
    /// they are pinned by their exact text rather than by a fragment of it.
    /// </summary>
    [Theory]
    [InlineData(true, true, "Text search in Claude Code: refused.")]
    [InlineData(false, true, "Text search in Claude Code: refused (left alone now — this run changes it).")]
    [InlineData(false, false, "Text search in Claude Code: left alone.")]
    [InlineData(true, false, "Text search in Claude Code: left alone (refused now — this run changes it).")]
    public void The_review_line_says_which_of_the_four_cases_this_is(
        bool registered,
        bool refuses,
        string opening)
    {
        var page = Page();
        page.ApplyHookState(registered, worktreeRegistered: false, Settings);
        page.SetChoice("claude", AgentChoice.McpAndInstructions);
        page.RefusesTextSearch = refuses;

        var line = page.GateReviewText;

        Assert.StartsWith(opening, line, StringComparison.Ordinal);
        Assert.Contains(Settings, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is printed on every run, including one that leaves the client alone entirely: on an
    /// update the clients page is folded away, so this line is the only place the effect is
    /// stated.
    /// </summary>
    [Fact]
    public void The_review_line_is_there_even_when_the_client_is_left_alone()
    {
        var page = Page();
        page.ApplyHookState(gateRegistered: true, worktreeRegistered: false, Settings);

        page.SetChoice("claude", AgentChoice.NoChange);

        Assert.StartsWith(
            "Text search in Claude Code: refused.",
            page.GateReviewText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The reversal line deliberately does not promise that `grep` works again: other entries may
    /// remain on the same event, and the line says only what the code checked.
    /// </summary>
    [Fact]
    public void Taking_the_entry_out_is_not_a_promise_that_searches_run_again()
    {
        var page = Page();
        page.ApplyHookState(gateRegistered: true, worktreeRegistered: false, Settings);
        page.SetChoice("claude", AgentChoice.McpAndInstructions);
        page.RefusesTextSearch = false;

        Assert.EndsWith(
            "anything else in that file stays.",
            page.GateReviewText,
            StringComparison.Ordinal);
    }

    private static AgentIntegrationPageViewModel Page() => new();

    private static AgentSnapshot Detected(AgentKind kind) =>
        new(
            kind,
            new FileMetadataSnapshot($@"C:\{kind}\client.exe", true, 1, null, null),
            FileMetadataSnapshot.Absent(string.Empty),
            FileMetadataSnapshot.Absent(string.Empty));
}
