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

    /// <summary>
    /// The phase says it is working to the record as well as to the console. `ReportPhase` wrote
    /// `progress.json` when the phase began and never again, so `index_status` printed
    /// `SemanticOverlay - not counted, recorded 4 min ago` in front of a build that was working —
    /// the same reading a build whose process died four minutes ago produces, and `SyncNotice`
    /// prints that age precisely so a dead build looks dead (#419).
    ///
    /// Both kinds of line are here on purpose. The heartbeat is not the only evidence of life: the
    /// per-project line of a long analysis arrives while the heartbeat is not running at all, so a
    /// record refreshed only by heartbeat ticks would still go stale through the longest stretch
    /// of the phase.
    /// </summary>
    [Fact]
    public void Every_line_the_phase_prints_also_records_that_it_is_still_running()
    {
        var console = new StringWriter();
        var recorded = 0;

        var narrate = CodeSearchSyncCommand.RoslynNarration(console, () => recorded++);
        narrate("Analysed 7/26 projects.");
        narrate("Still running, 15.0s so far.");

        Assert.Equal(2, recorded);
        Assert.Equal(
            "Roslyn: Analysed 7/26 projects." + Environment.NewLine +
            "Roslyn: Still running, 15.0s so far." + Environment.NewLine,
            console.ToString(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// And the wiring, which no behavioural test in this suite can reach: the semantic phase sits
    /// inside a sync, a sync needs an embedding backend, and the suite never starts one — the
    /// argument <c>ACommandWritesWhereItIsToldTests</c> already makes, for a defect of exactly
    /// this shape. So this guards the text instead: the prefix that marks a line as the C# phase's
    /// exists in one place, and that place is the sink that also records. A second spelling of it
    /// would be a line that prints while the record stands still.
    /// </summary>
    [Fact]
    public void The_phase_has_one_narration_sink()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "LocalAi.Cli", "CodeSearchSyncCommand.cs");
        var spellings = File.ReadAllLines(path)
            .Select((text, index) => (Line: index + 1, Text: text))
            .Where(entry => !entry.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(entry => entry.Text.Contains("\"Roslyn: {", StringComparison.Ordinal))
            .Select(entry => $"CodeSearchSyncCommand.cs:{entry.Line}:{entry.Text.Trim()}")
            .ToArray();

        Assert.True(
            spellings.Length == 1,
            "The C# phase must print through exactly one sink, the one that also records that the "
            + "phase is alive. Found:\n" + string.Join("\n", spellings));
    }

    /// <summary>
    /// Copied rather than shared with <c>ACommandWritesWhereItIsToldTests</c>, which has the same
    /// twelve lines: putting one helper in one place would mean a file under <c>tests/Shared</c>
    /// and a compile item in every project that wants it, which is more moving parts than the
    /// duplication costs.
    /// </summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binary.");
    }
}
