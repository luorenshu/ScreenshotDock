using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static async Task RunPinTestsAsync()
    {
        var frame = new DesktopFrame(EditorFixture(400, 220), new(0, 0, 400, 220));
        var outline = new ScreenshotAnnotation(CaptureTool.Arrow, new(30, 100), new(360, 100), 0xFFFF4D4F, 5, ArrowStyle: ArrowStyle.Outline);
        var image = AnnotationRenderer.Render(frame, frame.Bounds, [outline]);
        Check(!RedAt(image, 65, 106) && RedAt(image, 290, 105), "Hollow arrow has a narrow tail and a progressively wider shaft");
        Check(!RedAt(image, 290, 100) && RedAt(image, 360, 100), "Tapered hollow arrow leaves its interior transparent and renders its tip");
        Check(!AnnotationRenderer.HitTest(outline, new(290, 100), 0) && AnnotationRenderer.HitTest(outline, new(290, 105), 0),
            "Erasing a tapered hollow arrow follows the outline rather than its hollow interior");
        var reverse = outline with { Start = outline.End, End = outline.Start };
        var reversed = AnnotationRenderer.Render(frame, frame.Bounds, [reverse]);
        Check(RedAt(reversed, 90, 105) && !RedAt(reversed, 325, 106), "Hollow taper follows the drawing direction when the arrow points left");
        var document = new AnnotationDocument();
        document.Add(outline); document.RemoveAt(0); document.Undo();
        Check(ScreenCaptureService.EncodePng(AnnotationRenderer.Render(frame, frame.Bounds, document.Items)).SequenceEqual(ScreenCaptureService.EncodePng(image)),
            "Undo restores the same hollow tapered arrow pixels");
        File.WriteAllBytes(Path.Combine(_output, "hollow-tapered-arrow.png"), ScreenCaptureService.EncodePng(image));

        var previousForeground = GetForegroundWindow();
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var root = Path.Combine(_output, "app-data");
        var repository = new ScreenshotRepository(root);
        repository.Load(new(), DateTimeOffset.UtcNow); repository.Clear();
        new SettingsStore(root).Save(new() { AutoCopy = false, AutoExpand = false,
            PinHotkey = TestShortcut(0x7A),
            Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x77, KeyName = "F8" } });
        using var controller = new AppController(root);
        await controller.StartAsync();
        var fixture = new Window { Title = "ScreenshotDock pin fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, Topmost = true,
            Width = 600, Height = 330, Background = new SolidColorBrush(Color.FromRgb(221, 235, 255)) };
        var fixtureBounds = new PixelRect(monitor.Bounds.X + 300, monitor.Bounds.Y + 360, 600, 330);
        fixture.Show(); DesktopEnvironment.Place(fixture, fixtureBounds);
        await Task.Delay(80);
        var selection = new PixelRect(fixtureBounds.X + 20, fixtureBounds.Y + 20, 500, 260);

        async Task<CaptureWindow> Ready()
        {
            for (var i = 0; i < 100 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(10);
            var overlays = _application.Windows.OfType<CaptureWindow>().ToArray();
            foreach (var overlay in overlays)
            {
                DesktopEnvironment.DoNotActivate(overlay); DesktopEnvironment.MakeClickThrough(overlay); overlay.IsHitTestVisible = false;
            }
            var window = overlays.First(w => w.PointToScreen(new Point()).X == monitor.Bounds.X);
            window.Editor.Selection.SetSelection(selection);
            await Task.Delay(30);
            return window;
        }
        async Task<PinnedImageWindow> Pin(string label)
        {
            var existingPins = _application.Windows.OfType<PinnedImageWindow>().ToHashSet();
            var capture = controller.CaptureAsync();
            var window = await Ready();
            Check(!_application.Windows.OfType<PinnedImageWindow>().Any(w => w.IsVisible), "Existing pinned images are hidden while a new capture is selected: " + label);
            ClickEditor(window, "ArrowToolButton"); ClickEditor(window, "ArrowOutlineStyleButton");
            DrawEditorStroke(window.Editor, new(selection.X + 40, selection.Y + 85), new(selection.X + 420, selection.Y + 85));
            ClickEditor(window, "TextToolButton");
            window.Editor.BeginStroke(new(selection.X + 55, selection.Y + 155), 1);
            ((TextBox)window.FindName("AnnotationTextBox")).Text = label;
            SavePinToolbar((Border)window.FindName("CaptureToolbar"));
            ClickEditor(window, "PinCaptureButton");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(40);
            var pinned = _application.Windows.OfType<PinnedImageWindow>().Single(w => !existingPins.Contains(w));
            pinned.Activate();
            await Task.Delay(30);
            return pinned;
        }

        try
        {
            var first = await Pin("第一张 · 渐尖空心箭头");
            Check(first.Topmost && !first.ShowInTaskbar && first.IsVisible && !_application.Windows.OfType<CaptureWindow>().Any(),
                "Pin toolbar action exits capture and shows an independent topmost image");
            Check(first.Image.PixelWidth == 500 && first.Image.PixelHeight == 260 && RedAt(first.Image, 340, 90) && !RedAt(first.Image, 340, 85),
                "Pinned image includes annotations and keeps the hollow shaft and physical dimensions");
            var pixels = new byte[300 * 50 * 4];
            new FormatConvertedBitmap(first.Image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(55, 155, 300, 50), pixels, 300 * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 2] > 230 && pixels[i * 4 + 1] < 120) > 50,
                "Pinning commits pending Chinese text into the image");
            var padding = (int)Math.Ceiling(8 * monitor.Scale);
            Check(GetWindowRect(new WindowInteropHelper(first).Handle, out var placement)
                && placement.Left == selection.X - padding && placement.Top == selection.Y - padding
                && placement.Right - placement.Left == selection.Width + 2 * padding && placement.Bottom - placement.Top == selection.Height + 2 * padding,
                "Pin keeps the capture position and original image size with room for its outer shadow");
            var firstVisual = SaveVisual(first, "pinned-first.png");
            Check(BlueAt(firstVisual, 6, 40) && Pixel(firstVisual, 6, 40)[3] > Pixel(firstVisual, 3, 40)[3],
                "Selected pinned image has a blue outer shadow that fades away from the edge");

            var second = await Pin("第二张 · 可同时贴多张");
            Check(first.IsVisible && second.IsVisible && _application.Windows.OfType<PinnedImageWindow>().Count() == 2,
                "Pinning another capture restores the first and keeps both images visible");
            Check(!first.IsActive && second.IsActive, "Newest pin is selected independently of the earlier pin");
            var inactive = SaveVisual(first, "pinned-inactive.png");
            var active = SaveVisual(second, "pinned-selected.png");
            var whiteShadow = Pixel(inactive, 6, 40);
            Check(whiteShadow[3] > 0 && whiteShadow[0] > 240 && whiteShadow[1] > 240 && whiteShadow[2] > 240
                && whiteShadow[3] > Pixel(inactive, 3, 40)[3] && BlueAt(active, 6, 40),
                "Unselected pins have a soft white shadow while the selected pin has a blue shadow");
            SavePinComposition(inactive, active);
            repository.Load(new(), DateTimeOffset.UtcNow);
            Check(repository.Items.Count == 2, "Both annotated pins are also retained as ordinary cached screenshots");

            first.RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            await Task.Delay(30);
            Check(first.IsActive && !second.IsActive, "Clicking an earlier pin selects it and switches the other pin to its unselected state");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            first.RaiseEvent(escape);
            await Task.Delay(30);
            Check(escape.Handled && !first.IsVisible && second.IsVisible && _application.Windows.OfType<PinnedImageWindow>().Count() == 1,
                "Esc removes only the currently selected pin");
            repository.Load(new(), DateTimeOffset.UtcNow);
            Check(repository.Items.Count == 2, "Closing a pin does not delete either cached screenshot");

            var canceled = controller.CaptureAsync();
            var cancelWindow = await Ready();
            Check(!second.IsVisible, "A later capture temporarily hides remaining pins");
            ClickEditor(cancelWindow, "CancelCaptureButton");
            await canceled.WaitAsync(TimeSpan.FromSeconds(5));
            Check(second.IsVisible, "Canceling a later capture restores existing pins");
        }
        finally { fixture.Close(); controller.Dispose(); SetForegroundWindow(previousForeground); }
        Check(_application.Windows.Count == 0, "Disposing the app closes all pins, capture overlays and dock windows");
        Console.WriteLine($"{Checks.Count} pin and hollow-arrow checks passed.");
    }

    private static void SavePinComposition(BitmapSource inactive, BitmapSource active)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 50, 71)), null, new Rect(0, 0, 1000, 600));
            context.DrawImage(inactive, new Rect(40, 40, inactive.Width, inactive.Height));
            context.DrawImage(active, new Rect(400, 280, active.Width, active.Height));
        }
        var image = new RenderTargetBitmap(1000, 600, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        File.WriteAllBytes(Path.Combine(_output, "pinned-images-preview.png"), ScreenCaptureService.EncodePng(image));
    }

    private static void SavePinToolbar(Border toolbar)
    {
        toolbar.UpdateLayout();
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(toolbar), null, new Rect(0, 0, toolbar.ActualWidth, toolbar.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(toolbar);
        var image = new RenderTargetBitmap((int)Math.Ceiling(toolbar.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(toolbar.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(visual);
        if (Pixel(image, image.PixelWidth / 2, image.PixelHeight / 2)[3] == 0) throw new InvalidOperationException("Toolbar preview is empty.");
        File.WriteAllBytes(Path.Combine(_output, "pin-toolbar.png"), ScreenCaptureService.EncodePng(image));
    }
}
