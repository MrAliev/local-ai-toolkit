using LocalAi.Installer.Core.Agents;

namespace LocalAi.Installer.Core.Tests;

/// <summary>
/// What the installed block and skill tell an agent about the tools, checked against what the
/// tools do. Each row pins the claim that was false, not the sentence around it.
/// </summary>
public sealed class ManagedTextSaysWhatTheCodeDoesTests
{
    /// <summary>
    /// STALE compares the index with the tip of the manifest's mainline, not with this
    /// worktree's HEAD; a worktree that has moved off the base is refused rather than marked.
    /// </summary>
    [Fact]
    public void The_core_describes_stale_as_the_mainline_having_moved()
    {
        var core = Flat(ManagedInstructionBlock.Block);

        Assert.DoesNotContain("indexed commit is not HEAD", core, StringComparison.Ordinal);
        Assert.DoesNotContain("report the index as behind HEAD", core, StringComparison.Ordinal);
        Assert.Contains("mainline branch has moved since the index was built", core, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# is cut per type and member, and text outside every type reaches the index only in a
    /// file that declares none.
    /// </summary>
    [Fact]
    public void The_skill_says_where_csharp_outside_a_type_goes()
    {
        var skill = Flat(ManagedInstructionBlock.SkillBody);

        Assert.DoesNotContain("C# is always chunked by symbol", skill, StringComparison.Ordinal);
        Assert.Contains("is indexed only in a file that declares no type", skill, StringComparison.Ordinal);
    }

    /// <summary>The inline refresh refuses on a count of files, which is what the reader can check.</summary>
    [Fact]
    public void The_skill_states_the_refresh_bound_in_files()
    {
        Assert.Contains(
            "more than 200 files to re-read",
            Flat(ManagedInstructionBlock.SkillBody),
            StringComparison.Ordinal);
    }

    /// <summary>Triage takes the first context the broker will load, under whatever policy is set.</summary>
    [Fact]
    public void The_skill_does_not_promise_triage_probes_real_vram()
    {
        var skill = Flat(ManagedInstructionBlock.SkillBody);

        Assert.DoesNotContain("probes real VRAM capacity", skill, StringComparison.Ordinal);
        Assert.Contains("unless the residency policy was relaxed", skill, StringComparison.Ordinal);
    }

    private static string Flat(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
