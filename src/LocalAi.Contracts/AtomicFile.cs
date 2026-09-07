using System.Text.Json;

namespace LocalAi.Contracts;

/// <summary>
/// What a lost write costs, which is the only thing that decides how it is written.
///
/// The axis is recoverability, not importance. The generation pointer is as important as anything
/// in the runtime tree and still does not need a flush: every field of it is derived, and its id
/// is a hash of inputs the next run computes again from the same repository, so losing it costs
/// recomputation rather than a fact that existed nowhere else. The embedding checkpoint is the
/// opposite — the vectors in it were paid for in hours of a model's time and exist nowhere else
/// until the index is published — and it keeps its flush for that reason (#392, #399).
///
/// A retry budget is a separate question from durability, and answers a different one: whether a
/// reader can be holding the destination open when the replace lands. Windows raises
/// <see cref="UnauthorizedAccessException"/> for that — not <see cref="IOException"/>, and it does
/// not derive from one — so a write with a live reader and no budget fails outright, which is what
/// #427 was.
/// </summary>
public readonly record struct AtomicWrite
{
    private AtomicWrite(bool flushToDisk) => FlushToDisk = flushToDisk;

    /// <summary>
    /// Whether the bytes are handed to the disk rather than to the operating system's cache.
    /// Nothing in a test can observe this: it is a promise about a power cut.
    /// </summary>
    public bool FlushToDisk { get; private init; }

    /// <summary>
    /// How long the replace keeps trying while a reader denies it. Null means once — the common
    /// case, where nothing else opens the file, and where a loop would be paid for a race that
    /// cannot happen.
    /// </summary>
    public TimeSpan? RetryBudget { get; private init; }

    /// <summary>
    /// Losing this file costs work that can be done again. No flush.
    /// </summary>
    public static AtomicWrite Recomputable { get; } = new(flushToDisk: false);

    /// <summary>
    /// Losing this file loses something that existed nowhere else. Written through and flushed.
    /// </summary>
    public static AtomicWrite Irrecoverable { get; } = new(flushToDisk: true);

    /// <param name="budget">
    /// A budget rather than an attempt count, for the reason <c>BrokerRuntimeStateStore</c> gives:
    /// a count silently means different amounts of waiting depending on what else the machine is
    /// doing, and a budget means what it says.
    /// </param>
    public AtomicWrite Retrying(TimeSpan budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        return this with { RetryBudget = budget };
    }
}

/// <summary>
/// Replacing a file in one step: the content goes to a temporary beside the destination and is
/// renamed over it, so a reader sees either the whole previous document or the whole new one and
/// never a half-written file.
///
/// This existed twenty-eight times in five different shapes across the repository, and durability
/// was distributed by accident rather than by decision (#399). One shape now, and the call site
/// says which of the two states its file is in — see <see cref="AtomicWrite"/>.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Short enough that a reader releasing the file is noticed at once, long enough that the loop
    /// is not a spin. The figure <c>BrokerRuntimeStateStore</c> and <c>GenerationStore</c> both
    /// arrived at independently.
    /// </summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(25);

    public static void WriteJson<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        AtomicWrite write) =>
        Write(path, stream => JsonSerializer.Serialize(stream, value, options), write);

    /// <param name="content">
    /// Writes the whole document to the temporary. It is called exactly once, and anything it
    /// throws leaves the destination as it was.
    /// </param>
    public static void Write(string path, Action<Stream> content, AtomicWrite write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var (destination, temporary) = Prepare(path);
        try
        {
            using (var stream = Create(temporary, write, asynchronous: false))
            {
                content(stream);
                Finish(stream, write);
            }

            Replace(temporary, destination, write.RetryBudget);
        }
        finally
        {
            Discard(temporary);
        }
    }

    /// <summary>
    /// The same write for the one document produced asynchronously. The flush stays synchronous:
    /// <c>FlushAsync</c> reaches the operating system's cache and no further, so an async writer
    /// asking for durability and getting only that would be told a durability it did not have.
    /// </summary>
    public static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> content,
        AtomicWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var (destination, temporary) = Prepare(path);
        try
        {
            var stream = Create(temporary, write, asynchronous: true);
            await using (stream.ConfigureAwait(false))
            {
                await content(stream, cancellationToken).ConfigureAwait(false);
                Finish(stream, write);
            }

            Replace(temporary, destination, write.RetryBudget);
        }
        finally
        {
            Discard(temporary);
        }
    }

    private static (string Destination, string Temporary) Prepare(string path)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        // A fresh name per attempt, so two writers of the same file never collide on the
        // temporary and a leaked one is never mistaken for this attempt's.
        return (destination, destination + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private static FileStream Create(string temporary, AtomicWrite write, bool asynchronous)
    {
        var options = write.FlushToDisk ? FileOptions.WriteThrough : FileOptions.None;
        return new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            asynchronous ? options | FileOptions.Asynchronous : options);
    }

    private static void Finish(FileStream stream, AtomicWrite write)
    {
        if (write.FlushToDisk)
        {
            stream.Flush(flushToDisk: true);
        }
    }

    private static void Discard(string temporary)
    {
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }
    }

    private static void Replace(string temporary, string destination, TimeSpan? retryBudget)
    {
        if (retryBudget is not { } budget)
        {
            File.Move(temporary, destination, overwrite: true);
            return;
        }

        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException &&
                DateTime.UtcNow < deadline)
            {
                Thread.Sleep(RetryPause);
            }
        }
    }
}
