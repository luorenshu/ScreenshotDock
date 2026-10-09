using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.Windows.Input;

// Layered-window alpha keeps clicks transparent; wheel interception is scoped to its physical bounds.
public sealed class HotAreaWheelListener : IDisposable
{
    private readonly nint _window;
    private readonly Func<bool> _canHandle;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly NativeMethods.MouseHook _callback;
    private nint _hook;
    private int _generation;
    private bool _disposed;
    public event Action<int, bool>? Wheel;

    public HotAreaWheelListener(nint window, Func<bool> canHandle)
    {
        _window = window;
        _canHandle = canHandle;
        _callback = OnMouseInput;
    }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != 0) return;
        _hook = NativeMethods.SetWindowsHookEx(14, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用顶部热区滚轮控制。");
        _generation++;
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        if (_hook == 0) return;
        var hook = _hook;
        _hook = 0;
        _generation++;
        NativeMethods.UnhookWindowsHookEx(hook);
    }

    private nint OnMouseInput(int code, nint message, nint data)
    {
        var horizontal = message == 0x020E; // WM_MOUSEHWHEEL
        if (code >= 0 && (message == 0x020A || horizontal))
        {
            try
            {
                var input = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
                if (_hook != 0 && _canHandle() && !_dispatcher.HasShutdownStarted && Contains(input.Point))
                {
                    var delta = (short)(input.MouseData >> 16);
                    var generation = _generation;
                    // Return immediately; layout and scrolling run outside the low-level callback.
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (_hook != 0 && generation == _generation && _canHandle()) Wheel?.Invoke(delta, horizontal);
                    }));
                    return 1;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Hot-area wheel callback: {0}", exception.Message);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, message, data);
    }

    private bool Contains(NativeMethods.Point point)
    {
        if (!NativeMethods.GetWindowRect(_window, out var rect) || point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
            return false;

        // Settings, previews and application menus retain their own wheel behavior.
        var target = NativeMethods.WindowFromPoint(point);
        var root = NativeMethods.GetAncestor(target, 2); // GA_ROOT
        if (root != 0 && root != _window)
        {
            NativeMethods.GetWindowThreadProcessId(root, out var process);
            var clickThrough = (NativeMethods.GetWindowLongPtr(root, -20).ToInt64() & 0x20L) != 0;
            if (process == (uint)Environment.ProcessId && !clickThrough) return false;
        }
        return true;
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }
}
