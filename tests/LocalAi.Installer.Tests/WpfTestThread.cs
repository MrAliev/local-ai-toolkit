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
}
