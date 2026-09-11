using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The block used to assume a Git hook would refresh the index. Hooks are opt-in per
/// repository and installed by nobody unless asked, so on most machines nothing did.
/// </summary>
public sealed class TheIndexRefreshesWithoutAHookTests
{
    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Core, not skill: this fires the moment the agent merges or switches a branch, and
    /// consulting the skill would itself be the decision it has to reach first.
    /// </summary>
    [Theory]
    [InlineData("a repository may have no Git hook at all")]
    [InlineData("after a merge, a branch switch or edits you saved")]
    [InlineData("index_refresh")]
    public void The_core_says_to_refresh_once_the_tree_has_moved(string phrase) =>
        Assert.Contains(phrase, Flat(ManagedInstructionBlock.Block), StringComparison.Ordinal);

    /// <summary>Both promises were false wherever the hooks were never installed.</summary>
    [Theory]
    [InlineData("let the hook run")]
    [InlineData("the commit whose hook runs one")]
    public void The_core_no_longer_promises_a_hook_will_run(string phrase) =>
        Assert.DoesNotContain(phrase, Flat(ManagedInstructionBlock.Block), StringComparison.Ordinal);

    /// <summary>
    /// Why they stopped being the default: the sync runs while Git waits, so a commit made in
    /// an IDE starts an index build under the reader's hands.
    /// </summary>
    [Fact]
    public void The_skill_says_what_a_hook_costs_in_an_ide() =>
        Assert.Contains(
            "made in an IDE starts an index build there",
            Flat(ManagedInstructionBlock.SkillBody),
            StringComparison.Ordinal);

    /// <summary>
    /// The core requires a build's figures to reach the reader once a minute and told the agent
    /// to "arm something that emits them" — with nothing to arm: `repo status` was a single shot.
    /// A rule nothing can perform is worse than no rule.
    /// </summary>
    [Fact]
    public void The_core_names_the_command_that_emits_the_figures() =>
        Assert.Contains(
            "arm `repo status --follow`",
            Flat(ManagedInstructionBlock.Block),
            StringComparison.Ordinal);

    /// <summary>And the skill says how to call it, and what its exits mean.</summary>
    [Theory]
    [InlineData("localai repo status --root <repository> --follow")]
    [InlineData("--interval <seconds>")]
    [InlineData("exit 0 when the build finishes or when none is running")]
    public void The_skill_says_how_to_arm_it(string phrase) =>
        Assert.Contains(phrase, Flat(ManagedInstructionBlock.SkillBody), StringComparison.Ordinal);

    /// <summary>Still offered to whoever wants them — removed from the default, not the product.</summary>
    [Fact]
    public void Hooks_remain_documented_as_a_deliberate_choice() =>
        Assert.Contains(
            "localai hooks install --root",
            Flat(ManagedInstructionBlock.SkillBody),
            StringComparison.Ordinal);
}
