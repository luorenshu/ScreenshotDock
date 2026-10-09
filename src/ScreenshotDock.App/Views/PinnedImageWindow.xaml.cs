using System.Windows.Input;
using System.Windows.Media.Imaging;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

public partial class PinnedImageWindow : Window
{
    private readonly PixelRect _initialBounds;
    public BitmapSource Image { get; }

    public PinnedImageWindow(BitmapSource image, PixelRect imageBounds)
    {
        InitializeComponent();
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
    }

    private void PinnedMouseDown(object sender, MouseButtonEventArgs e)
    {
        Activate();
        e.Handled = true;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void PinnedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
