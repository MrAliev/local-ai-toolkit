using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// Codex reads the same `SKILL.md` format Claude does — verified on this machine: `codex
/// features list` reports `skill_search` stable, `~/.codex/skills/` exists, and the skill
/// already in it carries the same `name` + `description` frontmatter.
///
/// That matters because of what it costs not to. Codex had no skill, so everything a decision
/// needs was inlined into the block it reads on every session, which grew to 15398 characters
/// against a 15400 budget — two characters of room, and nothing new could be said to it at all.
/// The worktree section could not be added in #529 for exactly that reason.
///
/// So the block keeps only what has to be in context always, and the rest arrives the way it
/// arrives for Claude: through one file, written once, identical for both clients.
/// </summary>
public sealed class TheSkillReachesCodexTests : IDisposable
{
    private readonly string home = Path.Combine(
        Path.GetTempPath(),
        "localai-codex-skill-" + Guid.NewGuid().ToString("N"));

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData(AgentIntegrationChoice.InstructionsOnly)]
    [InlineData(AgentIntegrationChoice.McpAndInstructions)]
    public void The_instructions_bring_the_skill_with_them(AgentIntegrationChoice choice)
    {
        var plan = Adapter().Preview(choice);

        Assert.Contains(
            plan.Files,
            file => file.Path.EndsWith(
                Path.Combine(".codex", "skills", "localai", "SKILL.md"),
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A person who asked for server registrations and nothing else gets no file in a folder
    /// that may not exist yet. The skill is the instructions; it goes where they go.
    /// </summary>
    [Fact]
    public void Registering_servers_alone_writes_no_skill()
    {
        var plan = Adapter().Preview(AgentIntegrationChoice.McpOnly);

        Assert.DoesNotContain(
            plan.Files,
            file => file.Path.EndsWith("SKILL.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// One file for both clients: the same bytes, so one text stays true and one comparison
    /// decides whether a removal may delete it.
    /// </summary>
    [Fact]
    public void Both_clients_are_written_the_same_bytes()
    {
        var codex = Assert.Single(
            Adapter().Preview(AgentIntegrationChoice.InstructionsOnly).Files,
            file => file.Path.EndsWith("SKILL.md", StringComparison.Ordinal));
        var claude = Assert.Single(
            ClaudeAdapter().Preview(AgentIntegrationChoice.InstructionsOnly).Files,
            file => file.Path.EndsWith("SKILL.md", StringComparison.Ordinal));

        Assert.Equal(codex.AfterText, claude.AfterText, StringComparer.Ordinal);
    }

    /// <summary>
    /// The frontmatter names the skill as well as describing it. The skill already on this
    /// machine carries `name`, and if Codex requires it a file without one never loads while
    /// the block goes on telling Codex to use it — a failure with no symptom. Fifteen bytes
    /// against that is not a trade worth thinking about twice.
    /// </summary>
    [Fact]
    public void The_skill_names_itself_as_well_as_describing_itself()
    {
        var file = Assert.Single(
            Adapter().Preview(AgentIntegrationChoice.InstructionsOnly).Files,
            one => one.Path.EndsWith("SKILL.md", StringComparison.Ordinal));

        Assert.StartsWith("---\nname: localai\ndescription: ", file.AfterText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The block stops carrying what the skill carries. Measured rather than asserted vaguely:
    /// the whole point of the split is that the always-loaded half halves.
    /// </summary>
    [Fact]
    public void The_block_no_longer_repeats_the_skill()
    {
        var block = ManagedInstructionBlock.CodexBlock.ReplaceLineEndings("\n");

        Assert.DoesNotContain("### Transport and residency", block, StringComparison.Ordinal);
        Assert.DoesNotContain("### The tools in detail", block, StringComparison.Ordinal);
        Assert.True(
            block.Length < 8_400,
            $"the Codex block is {block.Length} characters; the split should have halved it");
    }

    /// <summary>
    /// And it points at the skill the way Claude's does, rather than at sections below it that
    /// are no longer there.
    /// </summary>
    [Fact]
    public void The_block_points_at_the_skill()
    {
        // The phrases that pointed at sections of the block itself, each of which is now in the
        // skill instead. Not the bare word "below" — the block legitimately says "which rule
        // below it overrides", and asserting on a word rather than on a claim is how a test
        // starts failing for reasons that have nothing to do with its subject.
        var codex = ManagedInstructionBlock.CodexBlock;
        Assert.DoesNotContain("at the end of this block", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("the setup described below", codex, StringComparison.Ordinal);
        Assert.DoesNotContain("most of them below", codex, StringComparison.Ordinal);
        Assert.Contains("skill", codex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Codex keeps one sentence Claude does not have, and it is enforcement rather than prose:
    /// Claude refuses `cat`, `tail -f` and `awk` over a whole log with a PreToolUse hook, and
    /// Codex has no such event, so on that side the sentence is the rule.
    /// </summary>
    [Fact]
    public void The_one_difference_from_the_core_is_the_log_readers()
    {
        Assert.NotEqual(ManagedInstructionBlock.Block, ManagedInstructionBlock.CodexBlock);
        Assert.Contains("`awk`", ManagedInstructionBlock.CodexBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The skill goes back off again. Installing a file into somebody's home directory and never
    /// taking it out is the half of the pair that is easy to forget, and forgetting it here would
    /// have left `~/.codex/skills/localai/SKILL.md` behind every uninstall — Claude's adapter
    /// has removed its copy since the skill existed.
    /// </summary>
    [Fact]
    public void The_skill_is_taken_off_again_when_localai_is_removed()
    {
        var path = Path.Combine(home, ManagedInstructionBlock.CodexSkillRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ManagedInstructionBlock.SkillFile(), Utf8);

        var plan = Adapter().PreviewRemoval();

        Assert.Contains(plan.Files, file => file.Path == path && file.Deletes);
    }

    private CodexConfigurationAdapter Adapter() => new(home, @"C:\LocalAi\bin");

    private ClaudeConfigurationAdapter ClaudeAdapter() => new(home, @"C:\LocalAi\bin");
}
