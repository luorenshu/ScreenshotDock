using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenshotDock.Core.Models;

namespace ScreenshotDock.Windows.Native;

public sealed record MonitorDescriptor(nint Handle, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);

public static class DesktopEnvironment
{
    public static PixelRect VirtualBounds => new(NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77),
        NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));

    public static PixelPoint Cursor
    {
        get { NativeMethods.GetCursorPos(out var point); return new(point.X, point.Y); }
    }

    public static bool IsMouseButtonDown => (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0
        || (NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0 || (NativeMethods.GetAsyncKeyState(0x04) & 0x8000) != 0;

    public static IReadOnlyList<MonitorDescriptor> Monitors
    {
        get
        {
            var monitors = new List<MonitorDescriptor>();
            NativeMethods.MonitorEnum callback = (nint monitor, nint dc, ref NativeMethods.Rect rect, nint data) =>
            {
                var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    NativeMethods.GetDpiForMonitor(monitor, 0, out var x, out _);
                    monitors.Add(new(monitor, Convert(info.Monitor), Convert(info.Work), Math.Max(1, x) / 96.0, (info.Flags & 1) != 0));
                }
                return true;
            };
            NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
            return monitors;
        }
    }

    public static MonitorDescriptor PrimaryMonitor => Monitors.First(m => m.IsPrimary);
    private static PixelRect Convert(NativeMethods.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static void Place(System.Windows.Window window, PixelRect bounds, bool topmost = true)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(handle, topmost ? new nint(-1) : 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | (topmost ? 0u : 0x0004u));
    }

    public static void DoNotActivate(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, -20, new nint(style | 0x08000000L | 0x00000080L));
    }

    public static void MakeClickThrough(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var style = NativeMethods.GetWindowLongPtr(handle, -20).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, -20, new nint(style | 0x00000020L)); // WS_EX_TRANSPARENT
    }

    public static void ExcludeFromCapture(System.Windows.Window window) =>
        NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(window).EnsureHandle(), 0x11);

    public static void FlushComposition() => NativeMethods.DwmFlush();

    public static bool IsFullscreenForeground()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var process);
        if (process == Environment.ProcessId) return false;
        var className = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(window, 2);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(window, out var rect)) return false;
        return CoversMonitor(Convert(rect), Convert(info.Monitor));
    }

    public static bool CoversMonitor(PixelRect window, PixelRect monitor) =>
        window.X <= monitor.X && window.Y <= monitor.Y && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
}
