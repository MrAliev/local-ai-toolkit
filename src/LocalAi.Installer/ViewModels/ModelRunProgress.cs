using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.ViewModels;

/// <summary>
/// Delivers a model run's events on its UI context. Closing flushes events already reported,
/// so the final batch sees their rows, then rejects later callbacks from that run.
/// Dispose is called on the owning context before final results are applied.
/// </summary>
public sealed class ModelRunProgress(
    Action<ModelProvisioningProgress> handler,
    SynchronizationContext? context = null) : IProgress<ModelProvisioningProgress>, IDisposable
{
    private readonly object gate = new();
    private readonly Queue<ModelProvisioningProgress> pending = new();
    private readonly Action<ModelProvisioningProgress> publish =
        handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly SynchronizationContext dispatch =
        context ?? SynchronizationContext.Current ?? new SynchronizationContext();
    private bool closed;

    public void Report(ModelProvisioningProgress step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (gate)
        {
            if (closed)
            {
                return;
            }

            pending.Enqueue(step);
        }

        dispatch.Post(_ => Deliver(), null);
    }

    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            DeliverPending();
        }
    }

    private void Deliver()
    {
        lock (gate)
        {
            DeliverPending();
        }
    }

    // Serialises delivery with closing even without a UI context. No asynchronous work runs
    // under this lock; the handler only updates the rows, progress caption and run log.
    private void DeliverPending()
    {
        while (pending.TryDequeue(out var step))
        {
            publish(step);
        }
    }
}
