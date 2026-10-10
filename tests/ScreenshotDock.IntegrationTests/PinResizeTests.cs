using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;
using WpfMouse = System.Windows.Input.Mouse;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static async Task RunPinResizeChecksAsync()
    {
        var foreground = GetForegroundWindow();
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var scale = monitor.Scale;
        var original = new PixelRect(monitor.WorkArea.X + 350, monitor.WorkArea.Y + 260, 400, 240);
        var pointer = new PixelPoint(original.X + 200, original.Y + 120);
        var source = CreateIllustration(400, 240, "拖动边缘缩放", "#D5E8FF");
        var sourceBytes = ScreenCaptureService.EncodePng(source);
        var first = new PinnedImageWindow(source, original, () => pointer);
        var second = new PinnedImageWindow(source, new(original.X + 520, original.Y + 160, 300, 180));
        first.Show(); second.Show(); ActivatePinFixture(first);
        DesktopEnvironment.MakeClickThrough(first); DesktopEnvironment.MakeClickThrough(second);
        await Task.Delay(40);

        PixelRect ImageBounds(PinnedImageWindow window)
        {
            window.UpdateLayout();
            var border = (Border)window.FindName("SelectionFrame");
            var a = border.PointToScreen(new Point());
            var b = border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight));
            return new((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y));
        }
        void Move() => first.RaiseEvent(new MouseEventArgs(WpfMouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = WpfMouse.MouseMoveEvent });
        void Button(bool down) => first.RaiseEvent(new MouseButtonEventArgs(WpfMouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = down ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseLeftButtonUpEvent });
        void Reset()
        {
            var padding = (int)Math.Ceiling(8 * scale);
            DesktopEnvironment.Place(first, new(original.X - padding, original.Y - padding, original.Width + padding * 2, original.Height + padding * 2));
            first.UpdateLayout();
        }

        try
        {
            var otherBounds = ImageBounds(second);
            foreach (var (name, edges, expectedCursor) in new[]
            {
                ("left", PinnedResizeEdge.Left, CaptureCursors.Horizontal),
                ("right", PinnedResizeEdge.Right, CaptureCursors.Horizontal),
                ("top", PinnedResizeEdge.Top, CaptureCursors.Vertical),
                ("bottom", PinnedResizeEdge.Bottom, CaptureCursors.Vertical),
                ("top-left", PinnedResizeEdge.Top | PinnedResizeEdge.Left, CaptureCursors.DiagonalDown),
                ("top-right", PinnedResizeEdge.Top | PinnedResizeEdge.Right, CaptureCursors.DiagonalUp),
                ("bottom-left", PinnedResizeEdge.Bottom | PinnedResizeEdge.Left, CaptureCursors.DiagonalUp),
                ("bottom-right", PinnedResizeEdge.Bottom | PinnedResizeEdge.Right, CaptureCursors.DiagonalDown)
            })
            {
                Reset();
                var horizontal = edges.HasFlag(PinnedResizeEdge.Left) || edges.HasFlag(PinnedResizeEdge.Right);
                var vertical = edges.HasFlag(PinnedResizeEdge.Top) || edges.HasFlag(PinnedResizeEdge.Bottom);
                pointer = new(edges.HasFlag(PinnedResizeEdge.Left) ? original.X : edges.HasFlag(PinnedResizeEdge.Right) ? original.Right - 1 : original.X + original.Width / 2,
                    edges.HasFlag(PinnedResizeEdge.Top) ? original.Y : edges.HasFlag(PinnedResizeEdge.Bottom) ? original.Bottom - 1 : original.Y + original.Height / 2);
                Move();
                Check(ReferenceEquals(first.Cursor, expectedCursor), "The " + name + " edge shows the corresponding resize cursor");
                Button(true);
                Check(first.IsMouseCaptured, "Dragging the " + name + " edge captures the pointer outside the original window");
                pointer = new(pointer.X + (horizontal ? edges.HasFlag(PinnedResizeEdge.Left) ? -100 : 100 : 0),
                    pointer.Y + (vertical ? edges.HasFlag(PinnedResizeEdge.Top) ? -60 : 60 : 0));
                Move();
                Button(false);
                await Task.Delay(10);
                var actual = ImageBounds(first);
                var expectedX = edges.HasFlag(PinnedResizeEdge.Left) ? original.X - 100 : horizontal ? original.X : original.X - 50;
                var expectedY = edges.HasFlag(PinnedResizeEdge.Top) ? original.Y - 60 : vertical ? original.Y : original.Y - 30;
                Check(actual == new PixelRect(expectedX, expectedY, 500, 300) && !first.IsMouseCaptured,
                    "Dragging the " + name + " edge preserves aspect and its opposite anchor and releases capture");
            }
            Check(ImageBounds(second) == otherBounds && second.IsVisible, "Resizing one pinned window leaves the other pin unchanged");
            Check(ScreenCaptureService.EncodePng(first.Image).SequenceEqual(sourceBytes) && first.Image.PixelWidth == 400 && first.Image.PixelHeight == 240,
                "Resizing changes only display size and retains the original image pixels");
            SaveVisual(first, "pinned-resized-selected.png");

            Reset(); pointer = new(original.Right - 1, original.Bottom - 1); Button(true);
            pointer = new(original.X - 500, original.Y - 500); Move(); Button(false);
            var minimum = ImageBounds(first);
            Check(minimum.Height >= (int)Math.Ceiling(32 * scale) && Math.Abs(minimum.Width / (double)minimum.Height - 400.0 / 240) < 0.03,
                "Shrinking across the opposite corner stops at a usable minimum without flipping or stretching");

            Reset(); pointer = new(original.X + 200, original.Y + 120); Move();
            Check(ReferenceEquals(first.Cursor, CaptureCursors.Move), "The image interior retains the move cursor");
            Button(true); pointer = new(pointer.X + 80, pointer.Y + 45); Move(); Button(false);
            Check(ImageBounds(first) == original with { X = original.X + 80, Y = original.Y + 45 }, "Dragging the interior moves the image without resizing it");

            Reset(); pointer = new(original.Right - 1, original.Y + 120); Button(true);
            pointer = new(pointer.X + 80, pointer.Y); Move();
            WpfMouse.Capture(null);
            var afterCaptureLoss = ImageBounds(first);
            pointer = new(pointer.X + 100, pointer.Y); Move();
            Check(!first.IsMouseCaptured && ImageBounds(first) == afterCaptureLoss, "Losing mouse capture ends resizing and prevents stale pointer movement");

            first.Hide(); first.Show(); await Task.Delay(20);
            Check(ImageBounds(first) == afterCaptureLoss, "Hiding and restoring a resized pin preserves its size and position");

            ActivatePinFixture(first); await Task.Delay(20);
            var active = SaveVisual(first, "pinned-resized-blue-shadow.png");
            ActivatePinFixture(second); await Task.Delay(20);
            var inactive = SaveVisual(first, "pinned-resized-white-shadow.png");
            var shadow = Pixel(inactive, 6, 40);
            Check(BlueAt(active, 6, 40) && shadow[3] > 0 && shadow[0] > 240 && shadow[1] > 240 && shadow[2] > 240,
                "A resized pin keeps the blue selected shadow and the white unselected shadow");

            ActivatePinFixture(first); pointer = new(afterCaptureLoss.Right - 1, afterCaptureLoss.Y + afterCaptureLoss.Height / 2); Button(true);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            first.RaiseEvent(escape);
            Check(escape.Handled && !first.IsVisible && !first.IsMouseCaptured && second.IsVisible,
                "Esc during a resize releases capture and closes only the selected pin");
        }
        finally { first.Close(); second.Close(); SetForegroundWindow(foreground); }
    }

    // Select only a window belonging to this test's UI thread, even when Windows denies foreground activation.
    private static void ActivatePinFixture(Window window) => SetActiveWindow(new WindowInteropHelper(window).Handle);
    [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint window);
}
