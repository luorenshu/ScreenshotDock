using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App.Capture;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

public partial class PinnedImageWindow : Window
{
    private readonly PixelRect _initialBounds;
    private readonly Func<PixelPoint> _pointer;
    private PixelRect _dragBounds;
    private PixelPoint _dragStart;
    private PinnedResizeEdge _resizeEdges;
    private bool _dragging;
    private int _minimumSide;
    public BitmapSource Image { get; }

    public PinnedImageWindow(BitmapSource image, PixelRect imageBounds, Func<PixelPoint>? pointer = null)
    {
        InitializeComponent();
        _pointer = pointer ?? (() => DesktopEnvironment.Cursor);
        Cursor = CaptureCursors.Move;
        Image = image;
        PinnedImage.Source = image;
        var monitors = DesktopEnvironment.Monitors;
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(new(imageBounds.X, imageBounds.Y)))
            ?? monitors.OrderByDescending(m => (long)m.Bounds.Intersect(imageBounds).Width * m.Bounds.Intersect(imageBounds).Height).First();
        var padding = (int)Math.Ceiling(8 * monitor.Scale);
        _initialBounds = new(imageBounds.X - padding, imageBounds.Y - padding,
            imageBounds.Width + 2 * padding, imageBounds.Height + 2 * padding);
        Width = _initialBounds.Width / monitor.Scale;
        Height = _initialBounds.Height / monitor.Scale;
        Left = _initialBounds.X / monitor.Scale;
        Top = _initialBounds.Y / monitor.Scale;
        SourceInitialized += (_, _) => DesktopEnvironment.Place(this, _initialBounds);
        Loaded += (_, _) => DesktopEnvironment.Place(this, _initialBounds);
        Closed += (_, _) => EndDrag();
    }

    private void PinnedMouseDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        e.Handled = true;
        if (_dragging) return;
        _dragStart = _pointer();
        _dragBounds = GetImageBounds();
        _resizeEdges = HitTest(_dragBounds, _dragStart);
        _minimumSide = (int)Math.Ceiling(32 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        _dragging = CaptureMouse();
        UpdateCursor(_resizeEdges);
    }

    private PixelRect GetImageBounds()
    {
        var topLeft = SelectionFrame.PointToScreen(new Point());
        var bottomRight = SelectionFrame.PointToScreen(new Point(SelectionFrame.ActualWidth, SelectionFrame.ActualHeight));
        return new((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
    }

    private PinnedResizeEdge HitTest(PixelRect bounds, PixelPoint point) =>
        PinnedImageGeometry.HitTest(bounds, point, (int)Math.Ceiling(6 * VisualTreeHelper.GetDpi(this).DpiScaleX));

    private void PinnedMouseMove(object sender, MouseEventArgs e)
    {
        var point = _pointer();
        if (!_dragging) { UpdateCursor(HitTest(GetImageBounds(), point)); return; }
        e.Handled = true;
        var delta = new PixelPoint(point.X - _dragStart.X, point.Y - _dragStart.Y);
        var bounds = _resizeEdges == PinnedResizeEdge.None
            ? _dragBounds with { X = _dragBounds.X + delta.X, Y = _dragBounds.Y + delta.Y }
            : PinnedImageGeometry.Resize(_dragBounds, _resizeEdges, delta, _minimumSide, Image.PixelWidth / (double)Image.PixelHeight);
        var padding = (int)Math.Ceiling(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        DesktopEnvironment.Place(this, new(bounds.X - padding, bounds.Y - padding, bounds.Width + 2 * padding, bounds.Height + 2 * padding));
    }

    private void PinnedMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        e.Handled = true;
        EndDrag();
    }

    private void PinnedCaptureLost(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, this)) _dragging = false;
    }
    private void EndDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void UpdateCursor(PinnedResizeEdge edges)
    {
        Cursor = edges switch
        {
            PinnedResizeEdge.Left or PinnedResizeEdge.Right => CaptureCursors.Horizontal,
            PinnedResizeEdge.Top or PinnedResizeEdge.Bottom => CaptureCursors.Vertical,
            (PinnedResizeEdge.Left | PinnedResizeEdge.Top) or (PinnedResizeEdge.Right | PinnedResizeEdge.Bottom) => CaptureCursors.DiagonalDown,
            (PinnedResizeEdge.Left | PinnedResizeEdge.Bottom) or (PinnedResizeEdge.Right | PinnedResizeEdge.Top) => CaptureCursors.DiagonalUp,
            _ => CaptureCursors.Move
        };
    }

    private void PinnedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
