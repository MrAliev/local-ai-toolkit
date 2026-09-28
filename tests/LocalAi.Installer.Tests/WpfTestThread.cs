using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace LocalAi.Installer.Tests;

internal static class WpfTestThread
{
    public static Task Run(Action action) => Task.Run(() =>
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A completion signal inside the STA delegate can race its finally block and native
        // thread teardown. Join from a pool thread so callers observe the complete lifetime.
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });

    /// <summary>
    /// Waits for a view model's work the way a window does: continuations come back to this
    /// thread's dispatcher, which keeps pumping meanwhile. Blocking on the task instead lets a
    /// continuation run on the pool, where the collections a window binds to refuse to change.
    /// </summary>
    public static void Wait(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = start();
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
