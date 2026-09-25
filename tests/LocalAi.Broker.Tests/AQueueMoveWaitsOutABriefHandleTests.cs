using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalAi.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Broker.Tests;

/// <summary>
/// #568. Every client runs <see cref="RuntimeAcl.Ensure"/> as it starts, outside the queue
/// mutex, and it rewrites job ACLs, holding a handle in a job directory for a moment. Windows
/// refuses to move a directory while one is held, whoever holds it.
/// </summary>
public sealed class AQueueMoveWaitsOutABriefHandleTests
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int AccessDenied = unchecked((int)0x80070005);

    public enum Held
    {
        // What a concurrent ACL rewrite of the directory does to the move: the #568 message.
        Directory,
        // What an ACL rewrite of a file inside it, or any reader of one, does.
        File,
    }

    [Theory]
    [InlineData(Held.Directory)]
    [InlineData(Held.File)]
    public async Task An_enqueue_commits_once_a_brief_handle_is_released(Held held)
    {
        SkipUnlessWindows();
        using var root = new TemporaryRuntimeRoot();
        using var scanner = new Scanner(releaseOnPause: true, pausesEnd: true);
        scanner.OnRead = () => HoldStagedJob(scanner, root.Path, held);
        var queue = new DurableQueue(root.Path, scanner);

        var result = await queue.EnqueueAsync(Request("brief"), TestContext.Current.CancellationToken);

        Assert.True(scanner.Took);
        Assert.Equal(1, scanner.Pauses);
        Assert.True(Directory.Exists(JobDirectory(root.Path, result.JobId)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root.Path, "staging")));
    }

    [Theory]
    [InlineData(Held.Directory, SharingViolation)]
    [InlineData(Held.File, AccessDenied)]
    public async Task An_enqueue_against_a_handle_that_stays_open_fails_with_the_move_error(
        Held held,
        int expected)
    {
        SkipUnlessWindows();
        using var root = new TemporaryRuntimeRoot();
        using var scanner = new Scanner(releaseOnPause: false, pausesEnd: true);
        scanner.OnRead = () => HoldStagedJob(scanner, root.Path, held);
        var queue = new DurableQueue(root.Path, scanner);
        var request = Request("held");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            queue.EnqueueAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.HResult);
        Assert.True(scanner.Pauses > 0);
        Assert.False(Directory.Exists(JobDirectory(root.Path, request.JobId)));
    }

    [Fact]
    public async Task Cancelling_an_enqueue_that_waits_on_a_held_handle_ends_the_wait()
    {
        SkipUnlessWindows();
        using var root = new TemporaryRuntimeRoot();
        // A pause that never ends by itself: only the token can finish this enqueue.
        using var scanner = new Scanner(releaseOnPause: false, pausesEnd: false);
        scanner.OnRead = () => HoldStagedJob(scanner, root.Path, Held.Directory);
        var queue = new DurableQueue(root.Path, scanner);
        var request = Request("cancelled");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var enqueue = Task.Run(() => queue.EnqueueAsync(request, cancellation.Token));
        if (await Task.WhenAny(scanner.PauseBegan.Task, enqueue) == enqueue)
        {
            await enqueue;
        }

        Assert.False(enqueue.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enqueue);
        Assert.Equal(1, scanner.Pauses);
        Assert.False(Directory.Exists(JobDirectory(root.Path, request.JobId)));
    }

    [Fact]
    public async Task A_move_refused_for_another_reason_fails_without_waiting()
    {
        SkipUnlessWindows();
        using var root = new TemporaryRuntimeRoot();
        using var scanner = new Scanner(releaseOnPause: false, pausesEnd: true);
        var request = Request("taken");
        scanner.OnRead = () =>
        {
            if (StagedRequest(root.Path) is not null)
            {
                Directory.CreateDirectory(JobDirectory(root.Path, request.JobId));
            }
        };
        var queue = new DurableQueue(root.Path, scanner);

        await Assert.ThrowsAsync<IOException>(() =>
            queue.EnqueueAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(0, scanner.Pauses);
    }

    [Theory]
    [InlineData(Held.Directory)]
    [InlineData(Held.File)]
    public async Task A_completed_job_is_archived_once_a_brief_handle_is_released(Held held)
    {
        SkipUnlessWindows();
        using var root = new TemporaryRuntimeRoot();
        using var scanner = new Scanner(releaseOnPause: true, pausesEnd: true);
        var queue = new DurableQueue(root.Path, scanner);
        var cancellationToken = TestContext.Current.CancellationToken;
        var enqueue = await queue.EnqueueAsync(Request("archived"), cancellationToken);
        var lease = Assert.IsType<LeasedJob>(await queue.LeaseNextAsync("worker", cancellationToken));
        var active = JobDirectory(root.Path, enqueue.JobId);
        scanner.Hold(active, held);

        await queue.CompleteAsync(
            enqueue.JobId,
            "worker",
            lease.LeaseId,
            JsonSerializer.SerializeToElement(true),
            cancellationToken);

        Assert.Equal(1, scanner.Pauses);
        Assert.False(Directory.Exists(active));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "archive", enqueue.JobId.ToString("N"))));
    }

    private static void SkipUnlessWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a directory held open.");

    private static LocalJobRequest Request(string key) =>
        LocalJobRequestFactory.CreateEmbed(key, LocalJobPriority.Foreground, "test-model", ["input"]);

    private static string JobDirectory(string root, Guid jobId) =>
        Path.Combine(root, "jobs", jobId.ToString("N"));

    private static string? StagedRequest(string root) =>
        Directory.EnumerateDirectories(Path.Combine(root, "staging"), "*.tmp")
            .Select(directory => Path.Combine(directory, "request.json"))
            .SingleOrDefault(File.Exists);

    // The queue reads the clock between staging a job and committing it.
    private static void HoldStagedJob(Scanner scanner, string root, Held held)
    {
        if (!scanner.Took && StagedRequest(root) is { } request)
        {
            scanner.Hold(Path.GetDirectoryName(request)!, held);
        }
    }

    /// <summary>
    /// Holds a handle in a job directory, and is the queue's clock so that it sees the queue
    /// pause: it can release the handle then, and it decides whether the pause ends.
    /// </summary>
    private sealed class Scanner(bool releaseOnPause, bool pausesEnd) : TimeProvider, IDisposable
    {
        private IDisposable? _handle;
        private int _pauses;

        public Action? OnRead { get; set; }

        public bool Took { get; private set; }

        public int Pauses => Volatile.Read(ref _pauses);

        public TaskCompletionSource PauseBegan { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold(string directory, Held held)
        {
            _handle = held == Held.Directory
                ? OpenDirectory(directory)
                : new FileStream(Path.Combine(directory, "request.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Took = true;
        }

        public override DateTimeOffset GetUtcNow()
        {
            OnRead?.Invoke();
            return base.GetUtcNow();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _pauses);
            if (releaseOnPause)
            {
                Release();
            }

            PauseBegan.TrySetResult();
            return base.CreateTimer(callback, state, pausesEnd ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
        }

        public void Dispose() => Release();

        private void Release()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        // FILE_LIST_DIRECTORY, and FILE_FLAG_BACKUP_SEMANTICS to open a directory at all.
        var handle = CreateFile(path, 0x1, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);
}
