using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LocalAi.Installer;

public static class WindowWorkArea
{
    /// <summary>Keep native chrome, WPF's DPI scaling, and the user's current monitor.</summary>
    public static void Follow(Window window) => _ = new Subscription(window);

    public static void Apply(Window window, Size physicalWorkArea, double dpi, Size preferredMinimum)
    {
        var width = physicalWorkArea.Width * 96 / dpi;
        var height = physicalWorkArea.Height * 96 / dpi;
        window.MinWidth = Math.Min(preferredMinimum.Width, width);
        window.MinHeight = Math.Min(preferredMinimum.Height, height);
        window.MaxWidth = width;
        window.MaxHeight = height;
        if (window.WindowState != WindowState.Normal) return;
        if (double.IsFinite(window.Width)) window.Width = Math.Clamp(window.Width, window.MinWidth, width);
        if (double.IsFinite(window.Height)) window.Height = Math.Clamp(window.Height, window.MinHeight, height);
    }

    // Native screen coordinates stay in pixels, including negative secondary-monitor origins.
    public static Rect FitBounds(Rect window, Rect workArea)
    {
        var width = Math.Min(window.Width, workArea.Width);
        var height = Math.Min(window.Height, workArea.Height);
        return new Rect(Math.Clamp(window.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(window.Top, workArea.Top, workArea.Bottom - height), width, height);
    }

    private sealed class Subscription
    {
        private readonly Window window;
        private readonly Size preferredMinimum;
        private HwndSource? source;
        private nint monitor;
        private bool queued;
        private bool closed;
        private bool dragging;

        public Subscription(Window window)
        {
            this.window = window;
            preferredMinimum = new Size(window.MinWidth, window.MinHeight);
            window.SourceInitialized += OnSourceInitialized;
            window.ContentRendered += OnRefresh;
            window.LocationChanged += OnLocationChanged;
            window.StateChanged += OnRefresh;
            window.DpiChanged += OnDpiChanged;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(OnMessage);
            Refresh(false);
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            if (source is not null && Native.MonitorFromWindow(source.Handle, 2) != monitor) QueueRefresh();
        }

        private void OnRefresh(object? sender, EventArgs e) => QueueRefresh();
        private void OnDpiChanged(object sender, DpiChangedEventArgs e) => QueueRefresh();

        private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            switch (message)
            {
                case 0x0231: dragging = true; break; // WM_ENTERSIZEMOVE
                case 0x0232: dragging = false; QueueRefresh(); break; // WM_EXITSIZEMOVE
                case 0x001A: // WM_SETTINGCHANGE: taskbar / working area
                case 0x007E: // WM_DISPLAYCHANGE: resolution / disconnected monitor
                    QueueRefresh(); break;
            }
            return 0; // WPF and the native caption still process every message.
        }

        private void QueueRefresh()
        {
            if (queued || closed || source is null) return;
            queued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                queued = false;
                if (!closed) Refresh(!dragging);
            }));
        }

        private void Refresh(bool fitPosition)
        {
            if (source is null) return;
            monitor = Native.MonitorFromWindow(source.Handle, 2); // MONITOR_DEFAULTTONEAREST
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var dpi = Native.GetDpiForWindow(source.Handle);
            if (dpi == 0 || !Native.GetMonitorInfo(monitor, ref info)) return;
            var work = info.Work.ToRect();
            if (work.Width <= 0 || work.Height <= 0) return;
            Apply(window, work.Size, dpi, preferredMinimum);
            if (!fitPosition || window.WindowState != WindowState.Normal ||
                !Native.GetWindowRect(source.Handle, out var rectangle)) return;
            var current = rectangle.ToRect();
            var fitted = FitBounds(current, work);
            if (current == fitted) return;
            Native.SetWindowPos(source.Handle, 0, (int)fitted.X, (int)fitted.Y,
                (int)fitted.Width, (int)fitted.Height, 0x0014); // NOZORDER | NOACTIVATE
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            closed = true;
            source?.RemoveHook(OnMessage);
            window.SourceInitialized -= OnSourceInitialized;
            window.ContentRendered -= OnRefresh;
            window.LocationChanged -= OnLocationChanged;
            window.StateChanged -= OnRefresh;
            window.DpiChanged -= OnDpiChanged;
            window.Closed -= OnClosed;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    private static class Native
    {
        [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    }
}
