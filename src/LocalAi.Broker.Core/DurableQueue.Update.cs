using LocalAi.Contracts;

namespace LocalAi.Broker;

public sealed partial class DurableQueue
{
    /// <summary>
    /// Stops new submissions while existing work drains, then holds the queue's original
    /// mutex until activation ends. Older clients do not know the admission gate, but they
    /// still obey that mutex, so none can enqueue between the final idle check and activation.
    /// The owning thread also releases both named mutexes if the updater process exits.
    /// </summary>
    public Task<IDisposable> AcquireUpdateLeaseAsync(
        bool wait,
        TimeSpan timeout,
        Action<int>? waiting = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var acquired = new TaskCompletionSource<IDisposable>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var stopped = new ManualResetEventSlim();
        var lease = new UpdateLease(release, stopped);
        // Mutex ownership belongs to a thread, not an async continuation. A dedicated
        // background thread keeps acquisition and release together across the install await.
        var owner = new Thread(() =>
        {
            var handedOff = false;
            try
            {
                ThrowIfAdmissionPaused(cancellationToken);
                using var admission = new Mutex(false, UpdateMutexName);
                using (EnterMutex(cancellationToken))
                using (EnterAdmissionProbe(cancellationToken))
                {
                    if (!TryEnter(admission))
                    {
                        throw new BrokerUpdateBusyException(0);
                    }
                }

                try
                {
                    var deadline = DateTimeOffset.UtcNow + timeout;
                    while (true)
                    {
                        int pending;
                        using (EnterMutex(cancellationToken))
                        {
                            MaintainActiveRoot(cancellationToken);
                            RecoverExpiredLeases(_timeProvider.GetUtcNow());
                            pending = ReadEntries().Count(entry =>
                                entry.State.State is LocalJobState.Queued or LocalJobState.Running);
                            if (pending == 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                handedOff = true;
                                acquired.SetResult(lease);
                                // Cancellation after handoff must not release the gate while
                                // activation is still unwinding or rolling back. Its finally
                                // disposes the lease once those operations have finished.
                                release.Wait();
                                return;
                            }
                        }

                        if (!wait || DateTimeOffset.UtcNow >= deadline)
                        {
                            throw new BrokerUpdateBusyException(pending, timedOut: wait);
                        }

                        waiting?.Invoke(pending);
                        if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }
                finally
                {
                    admission.ReleaseMutex();
                }
            }
            catch (OperationCanceledException)
            {
                acquired.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                acquired.TrySetException(exception);
            }
            finally
            {
                stopped.Set();
                if (!handedOff)
                {
                    release.Dispose();
                    stopped.Dispose();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Local AI update admission",
        };
        owner.Start();
        return acquired.Task;
    }

    private string UpdateMutexName => _mutexName + ".Update";

    private void ThrowIfAdmissionPaused(CancellationToken cancellationToken)
    {
        using var probe = EnterAdmissionProbe(cancellationToken);
        using var admission = new Mutex(false, UpdateMutexName);
        if (!TryEnter(admission))
        {
            throw new BrokerUpdateBusyException(0);
        }

        admission.ReleaseMutex();
    }

    private IDisposable EnterAdmissionProbe(CancellationToken cancellationToken)
    {
        // Probes briefly own the update mutex too. Serialize their entire acquire/release
        // and the updater's acquisition so only a real updater can look like a pause.
        var probe = new Mutex(false, UpdateMutexName + "-probe");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (probe.WaitOne(TimeSpan.FromMilliseconds(100)))
                    {
                        return new AdmissionProbeLease(probe);
                    }
                }
                catch (AbandonedMutexException)
                {
                    return new AdmissionProbeLease(probe);
                }
            }
        }
        catch
        {
            probe.Dispose();
            throw;
        }
    }

    private sealed class AdmissionProbeLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }

    private static bool TryEnter(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // A crashed updater cannot leave admission disabled.
            return true;
        }
    }

    private sealed class UpdateLease(
        ManualResetEventSlim release,
        ManualResetEventSlim stopped) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            release.Set();
            stopped.Wait();
            release.Dispose();
            stopped.Dispose();
        }
    }
}

public sealed class BrokerUpdateBusyException(int pendingJobs, bool timedOut = false)
    : IOException("broker_update_in_progress")
{
    public int PendingJobs { get; } = pendingJobs;

    public bool TimedOut { get; } = timedOut;
}
