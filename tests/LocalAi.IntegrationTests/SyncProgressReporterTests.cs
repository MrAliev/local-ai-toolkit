using LocalAi.Cli;
using LocalAi.Contracts;

namespace LocalAi.IntegrationTests;

public sealed class SyncProgressReporterTests
{
    [Fact]
    public void The_timer_reports_elapsed_time_every_fifteen_seconds_and_is_disposed()
    {
        var time = new ManualTime();
        var output = new StringWriter();
        var reporter = new SyncProgressReporter(_ => { }, output, time);
        reporter.Report(new("repo", RepositoryIndexProgressPhase.Planning, "root", 0, 0, 0, null,
            DateTimeOffset.UnixEpoch, Step: IndexBuildStep.ReadingRepository));
        Assert.Equal(TimeSpan.FromSeconds(15), time.DueTime);
        Assert.Equal(TimeSpan.FromSeconds(15), time.Period);
        output.GetStringBuilder().Clear();
        time.Advance();
        Assert.Contains("elapsed: 15s", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("%", output.ToString(), StringComparison.Ordinal);
        reporter.Dispose();
        Assert.True(time.Timer.Disposed);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public TimeSpan DueTime { get; private set; }
        public TimeSpan Period { get; private set; }
        public ManualTimer Timer { get; private set; } = null!;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return Timer = new ManualTimer(() => callback(state));
        }
        public void Advance()
        {
            _ticks += Period.Ticks;
            Timer.Fire();
        }
    }

    private sealed class ManualTimer(Action fire) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Fire() { if (!Disposed) fire(); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void Every_phase_reports_immediately_and_quiet_work_repeats_without_inventing_progress()
    {
        var output = new StringWriter();
        var saved = new List<RepositoryIndexProgress>();
        using var reporter = new SyncProgressReporter(saved.Add, output);
        foreach (var phase in Enum.GetValues<RepositoryIndexProgressPhase>())
        {
            output.GetStringBuilder().Clear();
            reporter.Report(new("repo", phase, "root", 0, 0, 0, null, DateTimeOffset.UnixEpoch));
            Assert.NotEmpty(output.ToString());
            Assert.DoesNotContain("0/0", output.ToString(), StringComparison.Ordinal);
            var first = output.ToString();
            var count = saved.Count;
            reporter.Tick();
            if (phase is RepositoryIndexProgressPhase.Completed or RepositoryIndexProgressPhase.Failed)
            {
                Assert.Equal(count, saved.Count);
                Assert.Equal(first, output.ToString());
            }
            else
            {
                Assert.Equal(count + 1, saved.Count);
                Assert.Equal(0, saved[^1].ProcessedChunks);
                Assert.Equal(0, saved[^1].TotalChunks);
                Assert.True(output.ToString().Length > first.Length);
            }
        }
    }

    [Fact]
    public void Latest_counts_are_used_and_disposal_stops_messages()
    {
        var output = new StringWriter();
        var reporter = new SyncProgressReporter(_ => { }, output);
        var progress = new RepositoryIndexProgress("repo", RepositoryIndexProgressPhase.SemanticOverlay,
            "root", 0, 0, 0, null, DateTimeOffset.UnixEpoch,
            Step: IndexBuildStep.AnalysingProjects, StepDone: 3, StepTotal: 12);
        reporter.Report(progress);
        Assert.Contains("3/12 (25%)", output.ToString(), StringComparison.Ordinal);
        reporter.Report(progress with { StepDone = 6 });
        output.GetStringBuilder().Clear();
        reporter.Tick();
        Assert.Contains("6/12 (50%)", output.ToString(), StringComparison.Ordinal);
        reporter.Dispose();
        output.GetStringBuilder().Clear();
        reporter.Tick();
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void All_steps_and_phases_are_localized_without_gaps()
    {
        Assert.Empty(SyncProgressReporter.Catalogue.Gaps());
        using var reading = LocalAi.Tests.Shared.TestCulture.Reading("ru");
        foreach (var step in Enum.GetValues<IndexBuildStep>())
        {
            var line = SyncProgressReporter.Format(new("repo", RepositoryIndexProgressPhase.SemanticBase,
                "root", 100, 100, 0, null, DateTimeOffset.UnixEpoch, Step: step));
            Assert.DoesNotContain(step.ToString(), line, StringComparison.Ordinal);
            Assert.DoesNotContain("100/100", line, StringComparison.Ordinal);
            Assert.DoesNotContain("%", line, StringComparison.Ordinal);
        }
    }
}
