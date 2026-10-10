using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.Windows.Capture;

public interface ICaptureRegionDetector
{
    PixelRect HitTest(PixelPoint point);
    Task<PixelRect?> FindControlAsync(PixelPoint point, CancellationToken cancellationToken);
}

// Capture native window order before showing overlays; querying WindowFromPoint afterwards would find the overlay.
public sealed class CaptureRegionDetector : ICaptureRegionDetector
{
    private sealed record WindowRegion(nint Handle, PixelRect Bounds, IReadOnlyList<PixelRect> Children);
    private static readonly SemaphoreSlim AutomationGate = new(1, 1);
    private readonly List<WindowRegion> _windows = [];
    private readonly IReadOnlyList<PixelRect> _monitors;
    private readonly PixelRect _desktop;

    private CaptureRegionDetector(PixelRect desktop)
    {
        _desktop = desktop;
        _monitors = DesktopEnvironment.Monitors.Select(m => m.Bounds.Intersect(desktop)).ToArray();
    }

    public static CaptureRegionDetector Capture(PixelRect desktop, IReadOnlyCollection<nint>? excludedWindows = null)
    {
        var detector = new CaptureRegionDetector(desktop);
        NativeMethods.WindowEnum enumerate = (handle, _) =>
        {
            if (excludedWindows?.Contains(handle) == true || !NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle)
                || (NativeMethods.GetWindowLongPtr(handle, -20).ToInt64() & 0x20) != 0) return true;
            if (NativeMethods.DwmGetWindowAttributeInt(handle, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var name = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(handle, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW") return true;
            if (NativeMethods.DwmGetWindowBounds(handle, 9, out var rect, Marshal.SizeOf<NativeMethods.Rect>()) != 0
                && !NativeMethods.GetWindowRect(handle, out rect)) return true;
            var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top).Intersect(desktop);
            if (bounds.Width < 2 || bounds.Height < 2) return true;
            var children = new List<PixelRect>();
            NativeMethods.WindowEnum enumerateChildren = (child, _) =>
            {
                if (NativeMethods.IsWindowVisible(child) && NativeMethods.GetWindowRect(child, out var childRect))
                {
                    var region = new PixelRect(childRect.Left, childRect.Top, childRect.Right - childRect.Left,
                        childRect.Bottom - childRect.Top).Intersect(bounds);
                    if (region.Width >= 2 && region.Height >= 2) children.Add(region);
                }
                return children.Count < 256;
            };
            NativeMethods.EnumChildWindows(handle, enumerateChildren, 0);
            detector._windows.Add(new(handle, bounds, children));
            return detector._windows.Count < 1024;
        };
        NativeMethods.EnumWindows(enumerate, 0);
        return detector;
    }

    private WindowRegion? WindowAt(PixelPoint point) => _windows.FirstOrDefault(w => w.Bounds.Contains(point));
    public PixelRect HitTest(PixelPoint point)
    {
        if (WindowAt(point) is { } window)
            return window.Children.Where(r => r.Contains(point)).Append(window.Bounds).MinBy(r => (long)r.Width * r.Height);
        return _monitors.FirstOrDefault(m => m.Contains(point));
    }

    public async Task<PixelRect?> FindControlAsync(PixelPoint point, CancellationToken cancellationToken)
    {
        if (WindowAt(point) is not { } window || cancellationToken.IsCancellationRequested || !AutomationGate.Wait(0)) return null;
        // A slow or hung provider must never block capture input or launch an unbounded number of workers.
        var query = Task.Run(() =>
        {
            try { return FindControl(window, point, cancellationToken); }
            catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException)
            { return (PixelRect?)null; }
            finally { AutomationGate.Release(); }
        });
        try { return await query.WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { return null; }
    }

    private PixelRect? FindControl(WindowRegion window, PixelPoint point, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;
        var root = AutomationElement.FromHandle(window.Handle);
        if (root is null) return null;
        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        var walker = TreeWalker.ControlViewWalker;
        var best = HitTest(point);
        var budget = Stopwatch.StartNew();
        var visited = 0;
        Walk(root, 0);
        return token.IsCancellationRequested ? null : best;

        void Walk(AutomationElement parent, int depth)
        {
            if (depth >= 16 || token.IsCancellationRequested || visited >= 256 || budget.ElapsedMilliseconds >= 200) return;
            var child = walker.GetFirstChild(parent, cache);
            while (child is not null && visited++ < 256 && !token.IsCancellationRequested && budget.ElapsedMilliseconds < 200)
            {
                var info = child.Cached;
                var region = ToPixels(info.BoundingRectangle, window.Bounds.Intersect(_desktop));
                if (!info.IsOffscreen && region.Contains(point) && region.Width >= 2 && region.Height >= 2)
                {
                    if ((long)region.Width * region.Height < (long)best.Width * best.Height) best = region;
                    // Keep the whole button/input rather than snapping to text inside it.
                    if (info.ControlType != ControlType.Button && info.ControlType != ControlType.Edit
                        && info.ControlType != ControlType.CheckBox && info.ControlType != ControlType.RadioButton
                        && info.ControlType != ControlType.ComboBox && info.ControlType != ControlType.Slider)
                        Walk(child, depth + 1);
                }
                else if (!info.IsOffscreen && info.BoundingRectangle.IsEmpty) Walk(child, depth + 1);
                child = walker.GetNextSibling(child, cache);
            }
        }
    }

    private static PixelRect ToPixels(Rect rect, PixelRect limits)
    {
        if (rect.IsEmpty || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top)
            || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)) return default;
        var left = (int)Math.Clamp(Math.Floor(rect.Left), limits.X, limits.Right);
        var top = (int)Math.Clamp(Math.Floor(rect.Top), limits.Y, limits.Bottom);
        var right = (int)Math.Clamp(Math.Ceiling(rect.Right), limits.X, limits.Right);
        var bottom = (int)Math.Clamp(Math.Ceiling(rect.Bottom), limits.Y, limits.Bottom);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
