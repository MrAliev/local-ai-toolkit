using System.Windows.Interop;
using System.Windows.Threading;

namespace LocalAi.Installer.Tests;

public sealed class WpfTestThreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_includes_dispatcher_shutdown_and_native_surface_cleanup(bool fails)
    {
        Dispatcher? dispatcher = null;
        Thread? worker = null;
        HwndSource? surface = null;
        var apartment = ApartmentState.Unknown;
        var failure = new InvalidOperationException("Fixture failure");
        var observed = await Record.ExceptionAsync(() => WpfTestThread.Run(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            worker = Thread.CurrentThread;
            apartment = worker.GetApartmentState();
            surface = new HwndSource(new HwndSourceParameters("STA lifetime regression")
            {
                WindowStyle = unchecked((int)0x80000000), Width = 20, Height = 20,
            });
            if (fails) throw failure;
        }));

        Assert.Same(fails ? failure : null, observed);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.HasShutdownFinished, "Returning a test result must include dispatcher shutdown.");
        Assert.NotNull(worker);
        Assert.False(worker.IsAlive, "No STA worker may outlive the reported test result.");
        Assert.NotNull(surface);
        Assert.True(surface.IsDisposed, "Dispatcher shutdown must release its native presentation source.");
    }
}
