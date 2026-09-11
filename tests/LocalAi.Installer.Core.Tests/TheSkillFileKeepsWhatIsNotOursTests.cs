using System.Text;
using LocalAi.Installer.Core.Agents;
using LocalAi.Installer.Core.Planning;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// The skill file was written and deleted whole, so an edit could only be honoured by leaving
/// the entire file behind — ours and theirs together, pointing at a launcher the uninstall had
/// just removed. It carries a managed block now, the way `CLAUDE.md` always has: an install
/// replaces our part, an uninstall takes our part out, and what somebody wrote around it is
/// never read as ours.
/// </summary>
public sealed class TheSkillFileKeepsWhatIsNotOursTests : IDisposable
{
    private const string Note = "## My own note\n\nKeep this.\n";

    private readonly string home = Path.Combine(
        Path.GetTempPath(),
        "localai-skillblock-" + Guid.NewGuid().ToString("N"));

    private string SkillPath => Path.Combine(home, ManagedInstructionBlock.ClaudeSkillRelativePath);

    private ClaudeConfigurationAdapter Adapter() =>
        new(home, @"C:\LocalAi\bin", TimeProvider.System);

    [Fact]
    public void An_install_replaces_our_part_and_keeps_theirs()
    {
        // A block from an earlier version, so there is something to replace. An install over
        // the current text plans nothing at all, which is the compare-and-swap working.
        Write(ManagedInstructionBlock.SkillFrontmatter +
            ManagedInstructionBlock.BeginMarker + "\nOld body.\n" +
            ManagedInstructionBlock.EndMarker + "\n" + Note);

        var after = Plan(Adapter().Preview(AgentIntegrationChoice.InstructionsOnly)).AfterText;

        Assert.Contains(Note, after, StringComparison.Ordinal);
        Assert.DoesNotContain("Old body.", after, StringComparison.Ordinal);
        Assert.Contains("### Transport and residency", after, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(after, ManagedInstructionBlock.BeginMarker));
    }

    [Fact]
    public void An_uninstall_takes_our_part_out_and_keeps_theirs()
    {
        Write(ManagedInstructionBlock.SkillFile() + Note);

        var plan = Plan(Adapter().PreviewRemoval());

        Assert.False(plan.Deletes, "the file holds writing that is not ours");
        Assert.Contains(Note, plan.AfterText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "### Transport and residency",
            plan.AfterText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ManagedInstructionBlock.BeginMarker,
            plan.AfterText,
            StringComparison.Ordinal);
    }

    /// <summary>Planning it is not doing it: this is what the file holds once apply has run.</summary>
    [Fact]
    public async Task Applying_the_removal_leaves_their_writing_on_disk()
    {
        Write(ManagedInstructionBlock.SkillFile() + Note);

        await AgentConfigurationFileOperations.ApplyAsync(
            Adapter().PreviewRemoval(),
            File.ReadAllBytes,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(SkillPath));
        var left = await File.ReadAllTextAsync(SkillPath, TestContext.Current.CancellationToken);
        Assert.Contains(Note, left, StringComparison.Ordinal);
        Assert.DoesNotContain("### Transport and residency", left, StringComparison.Ordinal);
    }

    /// <summary>Nothing of theirs in it, so nothing is owed: no husk of a skill left behind.</summary>
    [Fact]
    public void An_untouched_file_still_goes_entirely()
    {
        Write(ManagedInstructionBlock.SkillFile());

        Assert.True(Plan(Adapter().PreviewRemoval()).Deletes);
    }

    /// <summary>
    /// An older LocalAi wrote the body with no markers around it. Appending the block, which is
    /// what an unmarked file otherwise earns, would ship two copies of the same instructions.
    /// </summary>
    [Fact]
    public void A_file_from_before_the_markers_is_rewritten_rather_than_doubled()
    {
        Write("---\nname: localai\ndescription: Old.\n---\n\n### Transport and residency\n\nOld body.\n");

        var after = Plan(Adapter().Preview(AgentIntegrationChoice.InstructionsOnly)).AfterText;

        Assert.Equal(1, Occurrences(after, "### Transport and residency"));
        Assert.DoesNotContain("Old body.", after, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private AgentConfigurationFilePlan Plan(AgentConfigurationPlan plan) =>
        plan.Files.Single(file =>
            file.Path.Equals(SkillPath, StringComparison.OrdinalIgnoreCase));

    private void Write(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SkillPath)!);
        File.WriteAllText(SkillPath, content, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(
            Path.Combine(home, ".claude", "CLAUDE.md"),
            ManagedInstructionBlock.Upsert("Mine.\n").Content,
            new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
