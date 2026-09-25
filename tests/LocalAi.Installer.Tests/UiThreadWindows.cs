using System.Runtime.InteropServices;
using System.Text;

namespace LocalAi.Installer.Tests;

internal readonly record struct UiThreadWindow(nint Handle, uint Thread, string Class);

/// <summary>
/// The windows that exist only while a thread still holds UI state: the message window WPF gives
/// every Dispatcher until it shuts down, the windows of an undisposed HwndSource, and the window
/// COM gives a single-threaded apartment until its thread ends.
/// </summary>
internal static class UiThreadWindows
{
    private const nint MessageOnlyParent = -3;

    public static IReadOnlySet<UiThreadWindow> Snapshot()
    {
        var process = (uint)Environment.ProcessId;
        var found = new HashSet<UiThreadWindow>();
        void Consider(nint handle)
        {
            var thread = GetWindowThreadProcessId(handle, out var owner);
            if (owner != process) return;
            var name = new StringBuilder(256);
            if (GetClassName(handle, name, name.Capacity) == 0) return;
            var windowClass = name.ToString();
            if (windowClass.StartsWith("HwndWrapper[", StringComparison.Ordinal) || windowClass == "OleMainThreadWndClass")
                found.Add(new UiThreadWindow(handle, thread, windowClass));
        }

        EnumWindows((handle, _) => { Consider(handle); return true; }, 0);
        nint child = 0;
        while ((child = FindWindowEx(MessageOnlyParent, child, null, null)) != 0) Consider(child);
        return found;
    }

    public static uint CurrentThreadId() => GetCurrentThreadId();

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string? windowClass, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint process);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder name, int capacity);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
