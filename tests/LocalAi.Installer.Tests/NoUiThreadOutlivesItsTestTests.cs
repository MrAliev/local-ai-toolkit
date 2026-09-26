using System.Windows.Threading;
using Xunit.Sdk;

namespace LocalAi.Installer.Tests;

/// <summary>
/// A Dispatcher or a single-threaded apartment left on a thread keeps that thread's native windows
/// alive after the test that made it. These pin that the guard sees both while they run and stops
/// seeing them once the thread is gone, so a test that leaves one fails by name.
/// </summary>
public sealed class NoUiThreadOutlivesItsTestTests
{
    [Fact]
    public void A_running_dispatcher_is_seen_until_it_shuts_down_and_its_thread_ends()
    {
        var running = RunningDispatcher.Start();
        UiThreadWindow[] seen;
        try
        {
            seen = UiThreadWindows.Snapshot().Where(window => window.Thread == running.ThreadId).ToArray();
            Assert.Contains(seen, window => window.Class.StartsWith("HwndWrapper[", StringComparison.Ordinal));
        }
        finally { running.Stop(); }

        Assert.DoesNotContain(UiThreadWindows.Snapshot(), window => seen.Contains(window));
    }

    [Fact]
    public void A_single_threaded_apartment_is_seen_until_its_thread_ends()
    {
        uint threadId = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            threadId = UiThreadWindows.CurrentThreadId();
            entered.Set();
            release.Wait();
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        entered.Wait(TestContext.Current.CancellationToken);
        UiThreadWindow[] seen;
        try
        {
            seen = UiThreadWindows.Snapshot().Where(window => window.Thread == threadId).ToArray();
            Assert.Contains(seen, window => window.Class == "OleMainThreadWndClass");
        }
        finally
        {
            release.Set();
            worker.Join();
        }

        Assert.DoesNotContain(UiThreadWindows.Snapshot(), window => seen.Contains(window));
    }

    [Fact]
    public void The_walk_over_message_only_windows_goes_on_past_a_window_destroyed_under_it()
    {
        nint[] pair = [UiThreadWindows.CreateMessageOnlyWindow(), UiThreadWindows.CreateMessageOnlyWindow()];
        nint destroyed = 0;
        var visited = new List<nint>();
        try
        {
            UiThreadWindows.VisitMessageOnlyWindows(window =>
            {
                visited.Add(window);
                if (destroyed != 0 || !pair.Contains(window)) return;
                destroyed = window;
                UiThreadWindows.DestroyWindow(window);
            });
            Assert.Contains(destroyed, pair);
            Assert.Contains(pair.Single(window => window != destroyed), visited);
        }
        finally
        {
            foreach (var window in pair.Where(window => window != destroyed)) UiThreadWindows.DestroyWindow(window);
        }
    }

    [Fact]
    public void The_guard_fails_a_test_that_leaves_a_dispatcher_running_and_names_its_thread()
    {
        var guard = new NoUiThreadOutlivesItsTestAttribute();
        guard.Before(methodUnderTest: null!, test: null!);
        var running = RunningDispatcher.Start();
        try
        {
            var failure = Assert.Throws<FailException>(() => guard.After(methodUnderTest: null!, test: null!));
            Assert.Contains($"thread {running.ThreadId}", failure.Message, StringComparison.Ordinal);
        }
        finally { running.Stop(); }

        guard.After(methodUnderTest: null!, test: null!);
    }

    [Fact]
    public void The_guard_reports_the_windows_it_saw_appear_and_no_cause_it_did_not_check()
    {
        var guard = new NoUiThreadOutlivesItsTestAttribute();
        guard.Before(methodUnderTest: null!, test: null!);
        uint threadId = 0;
        using var created = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            threadId = UiThreadWindows.CurrentThreadId();
            var neverRun = Dispatcher.CurrentDispatcher;
            created.Set();
            release.Wait();
            neverRun.InvokeShutdown();
        }) { IsBackground = true };
        worker.Start();
        created.Wait(TestContext.Current.CancellationToken);
        try
        {
            var failure = Assert.Throws<FailException>(() => guard.After(methodUnderTest: null!, test: null!));
            var window = Assert.Single(UiThreadWindows.Snapshot(), window => window.Thread == threadId);
            Assert.Equal(
                $"Windows that appeared during this test are still open: {window.Class} on thread {threadId}. " +
                "Run WPF work through WpfTestThread.Run.",
                failure.Message);
        }
        finally
        {
            release.Set();
            worker.Join();
        }

        guard.After(methodUnderTest: null!, test: null!);
    }

    private sealed record RunningDispatcher(Dispatcher Dispatcher, Thread Worker, uint ThreadId)
    {
        public static RunningDispatcher Start()
        {
            Dispatcher? dispatcher = null;
            uint threadId = 0;
            using var running = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                threadId = UiThreadWindows.CurrentThreadId();
                dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(new Action(running.Set));
                Dispatcher.Run();
            }) { IsBackground = true };
            worker.Start();
            running.Wait();
            return new RunningDispatcher(dispatcher!, worker, threadId);
        }

        public void Stop()
        {
            Dispatcher.InvokeShutdown();
            Worker.Join();
        }
    }
}
