using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

/// <summary>
/// The instruction block requires a build's figures to reach the reader at least once a minute,
/// and told an agent to "arm something that emits them" — with nothing to arm. `repo status` was
/// a single shot answering CONFIGURED or NOT_CONFIGURED, and the progress a build writes on
/// every batch had no command that would read it twice.
/// </summary>
public sealed class FollowingTheIndexBuildTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private static RepositoryIndexProgress Building(
        RepositoryIndexProgressPhase phase = RepositoryIndexProgressPhase.EmbeddingBase) =>
        new(
            "repo-id",
            phase,
            @"R:\Repo",
            ProcessedChunks: 120,
            TotalChunks: 400,
            ChunksPerSecond: 8.5,
            EstimatedRemaining: TimeSpan.FromMinutes(2),
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Follow_is_off_unless_it_is_asked_for()
    {
        Assert.True(RepoCommand.TryParseStatusArguments([], out var target, out _));

        Assert.False(target.Follow);
    }

    [Fact]
    public void Follow_takes_an_interval_and_defaults_to_a_minute()
    {
        Assert.True(RepoCommand.TryParseStatusArguments(
            ["--follow"], out var minute, out _));
        Assert.True(RepoCommand.TryParseStatusArguments(
            ["--follow", "--interval", "30"], out var half, out _));

        Assert.True(minute.Follow);
        Assert.Equal(TimeSpan.FromSeconds(60), minute.Interval);
        Assert.Equal(TimeSpan.FromSeconds(30), half.Interval);
    }

    [Theory]
    // An interval with nothing after it, which would otherwise swallow the next argument.
    [InlineData("--follow", "--interval")]
    // Zero and below are not a cadence, and would spin the loop.
    [InlineData("--follow", "--interval", "0")]
    [InlineData("--follow", "--interval", "-5")]
    [InlineData("--follow", "--interval", "soon")]
    // An interval without a follow to pace is a request that was misunderstood, not one to
    // honour quietly.
    [InlineData("--interval", "30")]
    public void An_interval_that_paces_nothing_is_refused(params string[] arguments)
    {
        Assert.False(RepoCommand.TryParseStatusArguments(arguments, out _, out var refusal));

        Assert.NotNull(refusal);
    }

    /// <summary>A build still going keeps the loop alive and says where it is.</summary>
    [Fact]
    public void A_running_build_reports_and_carries_on()
    {
        var step = RepoFollowCommand.Step(Building(), TimeSpan.Zero, StaleAfter);

        Assert.Null(step.ExitCode);
        Assert.Contains("120", step.Line, StringComparison.Ordinal);
        Assert.Contains("400", step.Line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both terminal phases end the loop, and they do not end it the same way: a caller that
    /// only ever sees exit 0 cannot tell a finished build from a failed one.
    /// </summary>
    [Fact]
    public void A_finished_build_ends_the_loop_and_a_failed_one_ends_it_differently()
    {
        var done = RepoFollowCommand.Step(
            Building(RepositoryIndexProgressPhase.Completed), TimeSpan.Zero, StaleAfter);
        var failed = RepoFollowCommand.Step(
            Building(RepositoryIndexProgressPhase.Failed), TimeSpan.Zero, StaleAfter);

        Assert.Equal(0, done.ExitCode);
        // And not 2, which in this CLI means the command line was wrong — the refusals above
        // use it, and a build that failed is not a typo.
        Assert.Equal(RepoFollowCommand.BuildDidNotFinish, failed.ExitCode);
    }

    /// <summary>Nothing to follow is an answer, not a reason to wait for ever.</summary>
    [Fact]
    public void No_build_at_all_ends_the_loop_at_once()
    {
        var step = RepoFollowCommand.Step(null, TimeSpan.Zero, StaleAfter);

        Assert.Equal(0, step.ExitCode);
        Assert.NotEmpty(step.Line);
    }

    /// <summary>
    /// A build that died without writing `Failed` leaves a record that stops moving. Waiting on
    /// it for ever is the quiet minute the instruction block exists to forbid — a hung machine
    /// looking like a working one.
    /// </summary>
    [Fact]
    public void A_record_that_stopped_moving_ends_the_loop_rather_than_waiting_for_ever()
    {
        var step = RepoFollowCommand.Step(
            Building(),
            StaleAfter + TimeSpan.FromSeconds(1),
            StaleAfter);

        Assert.Equal(RepoFollowCommand.BuildDidNotFinish, step.ExitCode);
    }

    /// <summary>Numbers are quoted by agents and parsed by tests, so they stay invariant.</summary>
    [Fact]
    public void The_figures_are_not_localised()
    {
        var step = RepoFollowCommand.Step(Building(), TimeSpan.Zero, StaleAfter);

        Assert.DoesNotContain("8,5", step.Line, StringComparison.Ordinal);
        Assert.Contains("8.5", step.Line, StringComparison.Ordinal);
    }
}
