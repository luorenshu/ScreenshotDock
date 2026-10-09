using System.Windows.Media;
using System.Windows.Threading;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

public sealed class EdgeWindow : Window
{
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Func<PixelPoint> _cursor;
    private readonly Func<bool> _isMouseButtonDown;
    private PixelRect _triggerBounds;
    private long? _enteredAt;
    private bool _requested;
    public event Action? ExpandRequested;
    public EdgeWindow(Func<PixelPoint>? cursor = null, Func<bool>? isMouseButtonDown = null)
    {
        _cursor = cursor ?? (() => DesktopEnvironment.Cursor);
        _isMouseButtonDown = isMouseButtonDown ?? (() => DesktopEnvironment.IsMouseButtonDown);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 1000;
        Height = 4;
        IsHitTestVisible = false;
        SourceInitialized += (_, _) =>
        {
            DesktopEnvironment.DoNotActivate(this);
            DesktopEnvironment.MakeClickThrough(this);
            DesktopEnvironment.ExcludeFromCapture(this);
        };
        // Fully transparent windows do not receive mouse-enter events. Poll physical coordinates instead.
        _hover.Tick += (_, _) => CheckPointer();
        IsVisibleChanged += (_, _) =>
        {
            ResetHover();
            if (IsVisible) { PositionAtTop(); _hover.Start(); }
            else _hover.Stop();
        };
        Closed += (_, _) => { _hover.Stop(); ResetHover(); };
    }

    public void PositionAtTop()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        _triggerBounds = new(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, Math.Max(2, (int)Math.Ceiling(4 * monitor.Scale)));
        ResetHover();
        DesktopEnvironment.Place(this, _triggerBounds);
    }

    private void CheckPointer()
    {
        if (!IsVisible || !_triggerBounds.Contains(_cursor()) || _isMouseButtonDown()) { ResetHover(); return; }
        _enteredAt ??= Environment.TickCount64;
        if (_requested || Environment.TickCount64 - _enteredAt.Value < 180) return;
        _requested = true;
        ExpandRequested?.Invoke();
    }

    private void ResetHover() { _enteredAt = null; _requested = false; }
}
