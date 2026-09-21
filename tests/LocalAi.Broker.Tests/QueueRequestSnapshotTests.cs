using LocalAi.Contracts;

namespace LocalAi.Broker.Tests;

public sealed class QueueRequestSnapshotTests
{
    [Fact]
    public async Task Listing_uses_the_validated_payload_if_a_reader_locks_it_before_lease_recovery()
    {
        using var root = new TemporaryRuntimeRoot();
        var clock = new CallbackClock();
        var queue = new DurableQueue(root.Path, clock);
        var request = Request("snapshot");
        var ct = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(request, ct);
        var path = RequestPath(root.Path, request.JobId);
        FileStream? reader = null;
        clock.AfterAdmissionScan = () => reader = new FileStream(path,
            FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var candidate = Assert.Single(await queue.ListQueuedAsync(ct));
            Assert.Equal(request.JobId, candidate.Request.JobId);
            Assert.Equal("input", Assert.Single(Assert.IsType<EmbedJobPayload>(candidate.Request.Payload).Inputs));
        }
        finally
        {
            reader?.Dispose();
        }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("enqueue")]
    [InlineData("lease")]
    public async Task A_previous_listing_cannot_hide_same_length_request_substitution(string operation)
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var request = Request("substitution");
        var ct = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(request, ct);
        Assert.Single(await queue.ListQueuedAsync(ct));
        var path = RequestPath(root.Path, request.JobId);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var original = await File.ReadAllTextAsync(path, ct);
        var substituted = original.Replace(request.JobId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        Assert.NotEqual(original, substituted);
        Assert.Equal(original.Length, substituted.Length);
        await File.WriteAllTextAsync(path, substituted, ct);
        File.SetLastWriteTimeUtc(path, timestamp);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            switch (operation)
            {
                case "list": await queue.ListQueuedAsync(ct); break;
                case "enqueue": await queue.EnqueueAsync(Request("after-substitution"), ct); break;
                default: await queue.LeaseNextAsync("worker", ct); break;
            }
        });
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root.Path, "quarantine")));
        var next = await queue.EnqueueAsync(Request("after-recovery"), ct);
        Assert.False(next.JoinedExisting);
    }

    [Fact]
    public async Task A_previous_listing_does_not_hide_an_invalid_payload_or_hold_the_mutex_after_failure()
    {
        using var root = new TemporaryRuntimeRoot();
        var queue = new DurableQueue(root.Path);
        var request = Request("invalidated");
        var ct = TestContext.Current.CancellationToken;
        await queue.EnqueueAsync(request, ct);
        Assert.Single(await queue.ListQueuedAsync(ct));
        var path = RequestPath(root.Path, request.JobId);
        var original = await File.ReadAllTextAsync(path, ct);
        await File.WriteAllTextAsync(path, original.Replace("test-model", "          ", StringComparison.Ordinal), ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => queue.ListQueuedAsync(ct));

        // A different thread and queue instance must acquire the same named mutex after failure.
        await Task.Run(() => new DurableQueue(root.Path).EnqueueAsync(Request("after-failure"), ct), ct);
        Assert.Single(await queue.ListQueuedAsync(ct));
    }

    private static string RequestPath(string root, Guid id) =>
        Path.Combine(root, "jobs", id.ToString("N"), "request.json");

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(key, LocalJobPriority.Foreground, "test-model", ["input"]);

    private sealed class CallbackClock : TimeProvider
    {
        private int _calls;
        public Action? AfterAdmissionScan { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            // First clock read starts archive maintenance; the second starts expired-lease
            // recovery after committed requests have been validated. No wall-clock race.
            if (AfterAdmissionScan is not null && ++_calls == 2)
            {
                var action = AfterAdmissionScan;
                AfterAdmissionScan = null;
                action();
            }
            return DateTimeOffset.UtcNow;
        }
    }
}
