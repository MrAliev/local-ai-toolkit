using CodeSearch.Core.Resources;
using CodeSearch.Core.Semantics;
using LocalAi.Tests.Shared;

namespace CodeSearch.Tests;

/// <summary>
/// Observed on this machine, one second apart: `index_status` said `Status: current` and
/// `Navigation: precise (semantic.sidx есть)`, and then `find_references` said the generation's
/// semantic index could not be read. Both were true about different files. The generation's
/// `semantic.sidx` was intact — the status line had just read it. What was absent was the
/// **semantic overlay for the dirty tree**, a different file at a different path, which no sync
/// had built yet because the working tree had uncommitted edits (#409).
///
/// Three states shared one catch, and the everyday one — an edited worktree — wore the sentence
/// written for the alarming one. The remedy happened to be right for all three, which is why the
/// sentence survived: following it worked, so nobody questioned the line above it.
/// </summary>
public sealed class AMissingOverlayIsNotACorruptGenerationTests
{
    private const string Generation = "0123456789abcdef";
    private const string WorkingRoot = @"R:\repo";

    /// <summary>
    /// The three facts have to be three sentences. A reader who is told the generation's index is
    /// corrupt goes looking for a damaged index that is not damaged, and the honest reading of
    /// `index_status` beside it is that the tool contradicts itself.
    /// </summary>
    [Fact]
    public void The_three_states_do_not_share_a_sentence()
    {
        var corruptGeneration = IndexText.SemanticIndexUnreadableFact(Generation);
        var overlayNotBuilt = IndexText.SemanticOverlayNotBuiltFact(Generation);
        var overlayUnreadable = IndexText.SemanticOverlayUnreadableFact;

        Assert.NotEqual(corruptGeneration, overlayNotBuilt, StringComparer.Ordinal);
        Assert.NotEqual(corruptGeneration, overlayUnreadable, StringComparer.Ordinal);
        Assert.NotEqual(overlayNotBuilt, overlayUnreadable, StringComparer.Ordinal);
    }

    /// <summary>
    /// Nothing exists to rebuild when no overlay was ever built, and the house already has words
    /// for that shape of state — `GenerationNotPublishedRemedy` says "Build one".
    /// </summary>
    [Fact]
    public void The_remedy_for_an_overlay_that_was_never_built_does_not_say_rebuild()
    {
        Assert.Equal(
            IndexText.GenerationNotPublishedRemedy(WorkingRoot),
            IndexText.SemanticOverlayNotBuiltRemedy(WorkingRoot),
            StringComparer.Ordinal);
        Assert.NotEqual(
            IndexText.SemanticIndexUnreadableRemedy(WorkingRoot),
            IndexText.SemanticOverlayNotBuiltRemedy(WorkingRoot),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The half matters as much as the sentence: the MCP navigation tools print the fact alone
    /// while a build holds the sync gate, because a second `localai sync` exits 75 `sync_busy`.
    /// A fact with the command welded into it would take the `Remedy is null` early exit and name
    /// a sync the gate would refuse — the defect #383 fixed.
    /// </summary>
    [Fact]
    public void The_fact_does_not_carry_the_command()
    {
        Assert.DoesNotContain(
            "localai sync",
            IndexText.SemanticOverlayNotBuiltFact(Generation),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "localai sync",
            IndexText.SemanticOverlayUnreadableFact,
            StringComparison.Ordinal);
        Assert.Contains(
            "localai sync",
            IndexText.SemanticOverlayNotBuilt(Generation, WorkingRoot),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_refusals_follow_the_reader()
    {
        var english = (
            IndexText.SemanticOverlayNotBuilt(Generation, WorkingRoot),
            IndexText.SemanticOverlayUnreadable(WorkingRoot));

        using var reading = TestCulture.Reading("ru");

        Assert.NotEqual(
            english.Item1,
            IndexText.SemanticOverlayNotBuilt(Generation, WorkingRoot),
            StringComparer.Ordinal);
        Assert.NotEqual(
            english.Item2,
            IndexText.SemanticOverlayUnreadable(WorkingRoot),
            StringComparer.Ordinal);

        // The identifier and the path are not prose and do not move.
        Assert.Contains(
            Generation,
            IndexText.SemanticOverlayNotBuilt(Generation, WorkingRoot),
            StringComparison.Ordinal);
        Assert.Contains(
            WorkingRoot,
            IndexText.SemanticOverlayUnreadable(WorkingRoot),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The load-bearing reason the gateway needs two `try` blocks rather than one catch with more
    /// filters: `SemanticIndex.Load` opens the file, so a base `semantic.sidx` the manifest names
    /// and the disk lacks throws `FileNotFoundException` too. Filtering by exception type alone
    /// would report a missing *generation* index as a missing overlay — the same conflation, in
    /// mirror image.
    /// </summary>
    [Fact]
    public void A_base_index_that_is_absent_throws_the_same_type_as_an_absent_overlay()
    {
        var absent = Path.Combine(
            Path.GetTempPath(),
            "codesearch-absent-" + Guid.NewGuid().ToString("N") + ".sidx");

        Assert.Throws<FileNotFoundException>(() => SemanticIndex.Load(absent));
        Assert.Throws<FileNotFoundException>(() => SemanticIndexOverlay.Load(absent));
    }
}
