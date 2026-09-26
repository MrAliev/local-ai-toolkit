using System.ComponentModel;
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
    private const uint AncestorParent = 1;

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
        VisitMessageOnlyWindows(Consider);
        return found;
    }

    // Not a FindWindowEx walk: that one ends silently when the window it stands on is destroyed.
    internal static void VisitMessageOnlyWindows(Action<nint> visit) =>
        EnumChildWindows(MessageOnlyRoot, (handle, _) => { visit(handle); return true; }, 0);

    private static readonly nint MessageOnlyRoot = FindMessageOnlyRoot();

    private static nint FindMessageOnlyRoot()
    {
        var own = CreateMessageOnlyWindow();
        try { return GetAncestor(own, AncestorParent); }
        finally { DestroyWindow(own); }
    }

    public static uint CurrentThreadId() => GetCurrentThreadId();

    internal static nint CreateMessageOnlyWindow() =>
        CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, MessageOnlyParent, 0, 0, 0) is var window and not 0
            ? window
            : throw new Win32Exception();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint handle);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle, string windowClass, string? title, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint process);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder name, int capacity);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
