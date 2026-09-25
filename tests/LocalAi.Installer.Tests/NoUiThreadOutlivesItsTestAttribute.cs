using System.Reflection;
using Xunit.v3;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Fails a test after which a Dispatcher it started is still running, or a single-threaded
/// apartment it started is still alive, on any thread. WPF work belongs in
/// <see cref="WpfTestThread.Run"/>, which shuts the Dispatcher down on its own thread and joins it.
///
/// Only windows that appeared during the test count, so one leak is reported once, by the test
/// that made it. That holds because the assembly runs as one collection (GlobalUsings.cs).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class NoUiThreadOutlivesItsTestAttribute : BeforeAfterTestAttribute
{
    private IReadOnlySet<UiThreadWindow> before = new HashSet<UiThreadWindow>();

    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        before = UiThreadWindows.Snapshot();

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var left = UiThreadWindows.Snapshot().Where(window => !before.Contains(window)).ToArray();
        if (left.Length == 0) return;
        Assert.Fail(
            "A thread this test started still holds a running Dispatcher or a live single-threaded apartment. " +
            "Run WPF work through WpfTestThread.Run. Left behind: " +
            string.Join("; ", left.Select(window => $"{window.Class} on thread {window.Thread}")));
    }
}
