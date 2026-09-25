using LocalAi.Contracts;

namespace LocalAi.Broker;

/// <summary>Records every listing of the installed models for <c>doctor</c> to read.</summary>
public sealed class InstalledModelsRecorder(string runtimeRoot, TimeProvider? timeProvider = null)
{
    private readonly InstalledModelsSnapshotStore store = new(runtimeRoot);

    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Never throws for the file: the listing is what a job was waiting for, and an unwritable
    /// record only leaves doctor an older one, which it reports as stale.
    /// </summary>
    public void Record(IReadOnlyList<string> installedModels)
    {
        ArgumentNullException.ThrowIfNull(installedModels);
        try
        {
            store.Write(installedModels, timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Lists the installed models when the broker starts and again every
/// <see cref="InstalledModelsSnapshot.PassInterval"/>, so the record never ages past a pass while
/// a broker runs. The listing records itself; this only makes sure one happens.
/// </summary>
public sealed class InstalledModelsWatch(
    Func<CancellationToken, Task> look,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<CancellationToken, Task> look =
        look ?? throw new ArgumentNullException(nameof(look));

    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await look(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Ollama not answering: the record ages, and doctor says so.
            }

            try
            {
                await delay(InstalledModelsSnapshot.PassInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
