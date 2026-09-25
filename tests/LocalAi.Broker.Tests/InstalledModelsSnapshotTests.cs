using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

/// <summary>
/// What the broker last saw installed, kept for <c>doctor</c>, which must not start the broker to
/// ask. A valid catalogue used to be the whole of doctor's answer about models, and it read as
/// healthy while Ollama pointed at an empty model directory (#633).
/// </summary>
public sealed class InstalledModelsSnapshotTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "localai-installed-models-" + Guid.NewGuid().ToString("N"));

    public InstalledModelsSnapshotTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_listing_reads_back_with_the_time_it_was_seen()
    {
        var store = new InstalledModelsSnapshotStore(root);

        store.Write(["qwen3.5:9b", "gpt-oss:20b"], Noon);

        var snapshot = store.Read();
        Assert.NotNull(snapshot);
        Assert.Equal(Noon, snapshot.CheckedAtUtc);
        Assert.Equal(["qwen3.5:9b", "gpt-oss:20b"], snapshot.InstalledModels);
    }

    [Fact]
    public void Nothing_recorded_reads_as_nothing()
    {
        Assert.Null(new InstalledModelsSnapshotStore(root).Read());
    }

    [Fact]
    public void A_record_that_cannot_be_read_reads_as_nothing()
    {
        var store = new InstalledModelsSnapshotStore(root);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Null(store.Read());
    }

    /// <summary>
    /// Two passes, the rule the update check uses: a broker that is running looks once a pass,
    /// so a record older than two says nobody is looking any more.
    /// </summary>
    [Fact]
    public void A_record_older_than_two_passes_is_stale()
    {
        var snapshot = new InstalledModelsSnapshot(1, Noon, ["qwen3.5:9b"]);

        Assert.False(snapshot.IsStale(Noon + InstalledModelsSnapshot.PassInterval * 2));
        Assert.True(snapshot.IsStale(Noon + InstalledModelsSnapshot.PassInterval * 2 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_recorder_keeps_each_listing_with_the_time_it_happened()
    {
        var clock = new ManualTimeProvider(Noon);
        var recorder = new InstalledModelsRecorder(root, clock);

        recorder.Record(["qwen3.5:9b"]);
        clock.Advance(TimeSpan.FromMinutes(3));
        recorder.Record(["qwen3.5:9b", "gpt-oss:20b"]);

        var snapshot = new InstalledModelsSnapshotStore(root).Read();
        Assert.NotNull(snapshot);
        Assert.Equal(Noon.AddMinutes(3), snapshot.CheckedAtUtc);
        Assert.Equal(["qwen3.5:9b", "gpt-oss:20b"], snapshot.InstalledModels);
    }

    /// <summary>The listing is what a job was waiting for; the record of it must never fail it.</summary>
    [Fact]
    public void A_record_that_cannot_be_written_does_not_fail_the_listing()
    {
        Directory.CreateDirectory(new InstalledModelsSnapshotStore(root).FilePath);
        var recorder = new InstalledModelsRecorder(root, new ManualTimeProvider(Noon));

        var failure = Record.Exception(() => recorder.Record(["qwen3.5:9b"]));

        Assert.Null(failure);
    }

    [Fact]
    public async Task The_watch_looks_at_once_and_then_once_a_pass()
    {
        var looks = 0;
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();
        var watch = new InstalledModelsWatch(
            _ =>
            {
                looks++;
                return Task.CompletedTask;
            },
            (interval, _) =>
            {
                waits.Add(interval);
                if (waits.Count == 2)
                {
                    stop.Cancel();
                }

                return Task.CompletedTask;
            });

        await watch.RunAsync(stop.Token);

        Assert.Equal(2, looks);
        Assert.Equal([InstalledModelsSnapshot.PassInterval, InstalledModelsSnapshot.PassInterval], waits);
    }

    /// <summary>Ollama not answering is what makes the record age, and the watch outlives it.</summary>
    [Fact]
    public async Task A_look_that_fails_does_not_end_the_watch()
    {
        var looks = 0;
        using var stop = new CancellationTokenSource();
        var watch = new InstalledModelsWatch(
            _ =>
            {
                looks++;
                if (looks == 2)
                {
                    stop.Cancel();
                }

                throw new HttpRequestException("Ollama is not answering.");
            },
            (_, _) => Task.CompletedTask);

        await watch.RunAsync(stop.Token);

        Assert.Equal(2, looks);
    }
}
