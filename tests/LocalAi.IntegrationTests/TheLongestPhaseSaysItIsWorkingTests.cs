using CodeSearch.Core.Semantics;
using LocalAi.Cli;

namespace LocalAi.IntegrationTests;

/// <summary>
/// From `localai sync` to its first line of output: 36.72 seconds of complete silence, all of it
/// `dotnet restore` and `MSBuildWorkspace.OpenSolutionAsync` across 26 projects. Hidden indexing
/// is indistinguishable from a hung machine, and the reader cannot tell whether work is happening,
/// how much is left, or whether it is safe to interrupt (#387).
///
/// The embedding phase already reports itself well — chunks, a rate, an estimate. The longest
/// phase reported nothing at all.
///
/// What each of these pins is a fact the code already has and threw away, rather than a
/// fabricated fraction: the adapter durations are measured today and dropped, and the project
/// count is known before the loop over projects begins.
/// </summary>
public sealed class TheLongestPhaseSaysItIsWorkingTests
{
    /// <summary>
    /// `SemanticAdapterStatus.DurationMilliseconds` is populated by `ScipAdapterRunner` and was
    /// dropped on the floor by the line that reports the adapter — so the one phase with a
    /// measurement to its name printed everything about itself except how long it took.
    /// </summary>
    [Fact]
    public void An_adapter_says_how_long_it_took()
    {
        var line = CodeSearchSyncCommand.AdapterLine(
            new SemanticAdapterStatus(
                "typescript",
                SemanticAdapterState.Succeeded,
                "SCIP output imported.",
                28_400));

        Assert.Equal(
            "SCIP typescript: succeeded in 28.4s — SCIP output imported.",
            line,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Every element of it is a token, a number, or the adapter's own English message, so the
    /// line does not follow the reader's language — which is the same argument the repository
    /// already makes for the `SYNCED` and `REFUSED` lines.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1_500)]
    public void The_duration_is_invariant_and_one_decimal(int milliseconds)
    {
        using var reading = LocalAi.Tests.Shared.TestCulture.Reading("ru");

        var line = CodeSearchSyncCommand.AdapterLine(
            new SemanticAdapterStatus("python", SemanticAdapterState.Skipped, "no files", milliseconds));

        Assert.Contains(
            milliseconds == 0 ? " in 0.0s " : " in 1.5s ",
            line,
            StringComparison.Ordinal);
    }
}
