using System.Reflection;
using Xunit.v3;

namespace LocalAi.Installer.Tests;

/// <summary>
/// Fails a test that ends with a window of the kinds <see cref="UiThreadWindows"/> collects that
/// was not there when it began. WPF work belongs in <see cref="WpfTestThread.Run"/>, which shuts
/// the Dispatcher down on its own thread and joins it.
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
            "Windows that appeared during this test are still open: " +
            string.Join("; ", left.Select(window => $"{window.Class} on thread {window.Thread}")) +
            ". Run WPF work through WpfTestThread.Run.");
    }
}
