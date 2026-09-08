using CodeSearch.Mcp;
using LocalAi.Contracts;

namespace CodeSearch.Tests;

/// <summary>
/// While a build waits for a backend it is earning no estimate and embedding nothing, and
/// `IndexBuilder.Pausing` writes rate zero and a null estimate to say so. The table printed both
/// anyway: `Rate: 0.0 chunks/s` and `ETA: calculating`, one row above the `Paused:` row that
/// contradicts them.
///
/// The one-line form was already fixed this way — there the pause clause takes the estimate's
/// slot. A reader who sees both faces of one build must not be given two accounts of it.
/// </summary>
public sealed class APausedBuildPredictsNothingTests
{
    private const string Repository = "R:\\LocalAi";

    /// <summary>
    /// Nothing is being calculated, so nothing says it is. The row goes rather than gaining
    /// replacement text: any wording would put the pause's fact in two rows, one of them a
    /// prediction field carrying something that is not a prediction.
    /// </summary>
    [Fact]
    public void A_paused_build_promises_no_estimate()
    {
        var rows = CodeSearchTools.FormatProgress(Paused());

        Assert.DoesNotContain("ETA:", rows, StringComparison.Ordinal);
        Assert.Contains("Paused:", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// And prints no rate. Zero here is the writer's sentinel rather than a reading, and under a
    /// unit that implies measurement it says the machine has ground to a halt — true only in the
    /// sense the row below already explains, which is the arrangement being removed from the
    /// estimate one row up.
    /// </summary>
    [Fact]
    public void A_paused_build_reports_no_rate_it_did_not_measure()
    {
        var rows = CodeSearchTools.FormatProgress(Paused());

        Assert.DoesNotContain("Rate:", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows a reader acts on during an outage all stay: where it got to, why it stopped, and
    /// how fresh that account is. Removing a lie must not remove the state around it.
    /// </summary>
    [Fact]
    public void What_a_reader_acts_on_survives()
    {
        var rows = CodeSearchTools.FormatProgress(Paused());

        Assert.Contains("Progress:", rows, StringComparison.Ordinal);
        Assert.Contains("BackendUnavailable", rows, StringComparison.Ordinal);
        Assert.Contains("Updated:", rows, StringComparison.Ordinal);
        Assert.Contains("Sync phase:", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build that is embedding and has embedded something keeps both rows. This is the case the
    /// method's own summary defends — a measured average is the number the reader came for — and
    /// it must survive a change aimed at the state where nothing was measured.
    /// </summary>
    [Fact]
    public void A_running_build_still_reports_its_rate_and_its_estimate()
    {
        var rows = CodeSearchTools.FormatProgress(Running());

        Assert.Contains("Rate:", rows, StringComparison.Ordinal);
        Assert.Contains("ETA:", rows, StringComparison.Ordinal);
    }

    private static SyncSighting Paused() =>
        new(
            Repository,
            BuildHoldsTheGate: true,
            Progress() with
            {
                ChunksPerSecond = 0,
                EstimatedRemaining = null,
                Pause = IndexBuildPause.BackendUnavailable,
            });

    private static SyncSighting Running() =>
        new(Repository, BuildHoldsTheGate: true, Progress());

    private static RepositoryIndexProgress Progress() =>
        new(
            Repository,
            RepositoryIndexProgressPhase.EmbeddingBase,
            Repository,
            3120,
            8250,
            10.2,
            TimeSpan.FromMinutes(0.9),
            DateTimeOffset.UtcNow);
}
