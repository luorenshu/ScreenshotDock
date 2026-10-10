using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private sealed class RegionFixture(Func<PixelPoint, PixelRect> hit,
        Func<PixelPoint, CancellationToken, Task<PixelRect?>> refine) : ICaptureRegionDetector
    {
        public PixelRect HitTest(PixelPoint point) => hit(point);
        public Task<PixelRect?> FindControlAsync(PixelPoint point, CancellationToken token) => refine(point, token);
    }

    private static PixelRect ElementBounds(FrameworkElement element)
    {
        var first = element.PointToScreen(new Point());
        var last = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new((int)Math.Round(first.X), (int)Math.Round(first.Y), (int)Math.Round(last.X - first.X), (int)Math.Round(last.Y - first.Y));
    }

    private static async Task RunSmartSelectionTestsAsync()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var desktop = DesktopEnvironment.VirtualBounds;
        var foreground = GetForegroundWindow();
        var content = new Canvas { Background = Brushes.White };
        var button = new Button { Content = "自动识别按钮", Width = 160, Height = 44 };
        Canvas.SetLeft(button, 40); Canvas.SetTop(button, 50); content.Children.Add(button);
        var input = new TextBox { Text = "独立输入区域", Width = 220, Height = 48, Padding = new Thickness(8) };
        Canvas.SetLeft(input, 260); Canvas.SetTop(input, 50); content.Children.Add(input);
        var list = new ListBox { Width = 440, Height = 160 };
        list.Items.Add("识别到区域后，单击确认"); list.Items.Add("拖动鼠标仍然可以自由框选");
        Canvas.SetLeft(list, 40); Canvas.SetTop(list, 130); content.Children.Add(list);
        var fixture = new Window { Title = "ScreenshotDock smart selection fixture", Content = content, Width = 600, Height = 400,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        DesktopEnvironment.DoNotActivate(fixture);
        fixture.Show();
        DesktopEnvironment.Place(fixture, new(monitor.Bounds.X + 240, monitor.Bounds.Y + 140, (int)(600 * monitor.Scale), (int)(400 * monitor.Scale)));
        CaptureWindow? window = null;
        try
        {
            await Task.Delay(80);
            var nativeBounds = ElementBounds(fixture);
            var buttonBounds = ElementBounds(button);
            var inputBounds = ElementBounds(input);
            var point = new PixelPoint(buttonBounds.X + buttonBounds.Width / 2, buttonBounds.Y + buttonBounds.Height / 2);
            var inputPoint = new PixelPoint(inputBounds.X + inputBounds.Width / 2, inputBounds.Y + inputBounds.Height / 2);
            var detector = CaptureRegionDetector.Capture(desktop);
            Check(detector.HitTest(point) == nativeBounds, "Native snapshot immediately recognizes the topmost WPF window");
            Check(await detector.FindControlAsync(point, CancellationToken.None) == buttonBounds,
                "UI Automation recognizes the whole button rather than only its label");
            Check(await detector.FindControlAsync(inputPoint, CancellationToken.None) == inputBounds,
                "UI Automation recognizes the whole editable input region");

            var blocker = new Window { Width = 100, Height = 80, Background = Brushes.LightBlue, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
            DesktopEnvironment.DoNotActivate(blocker);
            blocker.Show();
            DesktopEnvironment.Place(blocker, new(point.X - 30, point.Y - 30, 100, 80));
            await Task.Delay(50);
            var blockedDetector = CaptureRegionDetector.Capture(desktop);
            Check(blockedDetector.HitTest(point) == ElementBounds(blocker),
                "A foreground window wins over controls in an occluded background window");
            Check(await blockedDetector.FindControlAsync(point, CancellationToken.None) == ElementBounds(blocker),
                "A window without detectable controls retains its window-level selection");
            DesktopEnvironment.MakeClickThrough(blocker);
            Check(CaptureRegionDetector.Capture(desktop).HitTest(point) == nativeBounds,
                "Transparent click-through overlays are excluded from native candidates");
            blocker.Close();
            var excluded = CaptureRegionDetector.Capture(desktop, [new WindowInteropHelper(fixture).Handle]);
            Check(excluded.HitTest(point) != nativeBounds, "Explicitly excluded windows do not become selection targets");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Check(await detector.FindControlAsync(point, canceled.Token) is null,
                "Canceled capture queries do not start another UI Automation worker");

            var fixtureImage = SaveVisual(fixture, "smart-native-fixture.png");
            var scene = new DrawingVisual();
            using (var drawing = scene.RenderOpen())
            {
                drawing.DrawImage(EditorFixture(desktop.Width, desktop.Height), new Rect(0, 0, desktop.Width, desktop.Height));
                drawing.DrawImage(fixtureImage, new Rect(nativeBounds.X - desktop.X, nativeBounds.Y - desktop.Y, nativeBounds.Width, nativeBounds.Height));
            }
            var source = new RenderTargetBitmap(desktop.Width, desktop.Height, 96, 96, PixelFormats.Pbgra32);
            source.Render(scene); source.Freeze();
            var frame = new DesktopFrame(source, desktop);
            var selection = new SelectionSession(desktop);
            using var editor = new CaptureEditSession(selection);
            using var smart = new SmartSelectionSession(editor, detector);
            window = new CaptureWindow(frame, monitor, selection, editor, smart) { ShowActivated = false, IsHitTestVisible = false };
            DesktopEnvironment.DoNotActivate(window);
            DesktopEnvironment.MakeClickThrough(window);
            window.Show();
            smart.Hover(point);
            await Task.Delay(400);
            Check(smart.Preview == buttonBounds && selection.Selection.IsEmpty,
                "Hover refines the preview without committing a selection or editing history");
            Check(((Border)window.FindName("CaptureToolbar")).Visibility == Visibility.Collapsed,
                "The annotation toolbar stays hidden during automatic hover preview");
            Check(await detector.FindControlAsync(inputPoint, CancellationToken.None) == inputBounds,
                "Underlying controls remain detectable after a capture overlay has been shown");
            SaveVisual(window, "smart-selection-preview.png");
            smart.Begin(point, 4 * monitor.Scale);
            smart.End(new(point.X + 1, point.Y + 1));
            await Task.Delay(80);
            Check(selection.Selection == buttonBounds && smart.Preview.IsEmpty,
                "A click with slight mouse jitter confirms the detected control bounds");
            Check(((Border)window.FindName("CaptureToolbar")).IsVisible,
                "Confirming the automatic selection opens the existing annotation toolbar");
            smart.Hover(inputPoint);
            await Task.Delay(160);
            Check(selection.Selection == buttonBounds && smart.Preview.IsEmpty,
                "Hovering elsewhere after confirmation preserves the chosen region");
            var exported = AnnotationRenderer.Render(frame, selection.Selection, editor.Document.Items);
            var original = ScreenCaptureService.Crop(frame, buttonBounds);
            Check(exported.PixelWidth == buttonBounds.Width && exported.PixelHeight == buttonBounds.Height
                && Pixel(exported, 0, 0).SequenceEqual(Pixel(original, 0, 0)),
                "Export uses the detected pixel bounds without preview borders or UI");
            SaveVisual(window, "smart-selection-confirmed.png");
            selection.Begin(point, 0); selection.Update(new(point.X + 20, point.Y + 15)); selection.End();
            Check(selection.Selection.X == buttonBounds.X + 20 && selection.Selection.Y == buttonBounds.Y + 15,
                "An automatically confirmed selection can still be dragged to another position");
            selection.Begin(new(selection.Selection.Right, selection.Selection.Bottom), 5);
            selection.Update(new(selection.Selection.Right + 30, selection.Selection.Bottom + 20)); selection.End();
            Check(selection.Selection.Width == buttonBounds.Width + 30 && selection.Selection.Height == buttonBounds.Height + 20,
                "An automatically confirmed selection can still be resized at its edges");
            File.WriteAllText(Path.Combine(_output, "smart-selection-bounds.json"), JsonSerializer.Serialize(new
            { NativeWindow = nativeBounds, Button = buttonBounds, Input = inputBounds, PreviewConfirmed = true }));
            window.Close(); window = null; fixture.Close();
            Check(await detector.FindControlAsync(point, CancellationToken.None) is null,
                "A target window closing during capture is handled without interrupting selection");
        }
        finally { window?.Close(); fixture.Close(); SetForegroundWindow(foreground); }

        await VerifySmartSelectionTransitionsAsync();
        Check(_application.Windows.Count == 0, "Smart selection windows and fixtures are closed after verification");
        Console.WriteLine($"{Checks.Count} smart selection checks passed.");
    }

    private static async Task VerifySmartSelectionTransitionsAsync()
    {
        var bounds = new PixelRect(-1920, 0, 3840, 1080);
        var region = new PixelRect(-400, 100, 600, 300);
        var refined = new PixelRect(-350, 130, 160, 60);
        var point = new PixelPoint(-320, 150);
        var selection = new SelectionSession(bounds);
        using var editor = new CaptureEditSession(selection);
        var late = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new RegionFixture(_ => region, (_, _) => late.Task);
        using var smart = new SmartSelectionSession(editor, detector);
        smart.Hover(point);
        Check(smart.Preview == region && selection.Selection.IsEmpty, "Window preview appears immediately on a negative-origin desktop");
        await Task.Delay(100);
        smart.Begin(point, 6);
        late.SetResult(refined);
        await Task.Delay(80);
        smart.End(point);
        Check(selection.Selection == region, "Late control detection cannot change the region already frozen by mouse-down");

        selection.SetSelection(default);
        smart.Hover(point);
        await Task.Delay(120);
        smart.Begin(point, 6);
        smart.Update(new(point.X + 80, point.Y + 50));
        Check(selection.Action == SelectionAction.New && smart.Preview.IsEmpty,
            "Dragging switches from automatic preview to free selection before release");
        smart.End(new(point.X + 100, point.Y + 70));
        Check(selection.Selection == new PixelRect(point.X, point.Y, 100, 70) && selection.Action == SelectionAction.None,
            "Free drag honors the actual pointer path instead of the recognized control");

        selection.SetSelection(default); smart.Hover(point); smart.Begin(point, 6);
        smart.End(new(point.X - 40, point.Y - 25));
        Check(selection.Selection == new PixelRect(point.X - 40, point.Y - 25, 40, 25),
            "A reverse drag recognized only at mouse-up still produces the correct free selection");
        selection.SetSelection(default); smart.Hover(point); smart.Begin(point, 6); smart.CancelPointer();
        Check(selection.Selection.IsEmpty && !smart.IsSelecting, "Losing mouse capture cancels an unconfirmed automatic click");
        selection.SetSelection(default); smart.Hover(point); await Task.Delay(120); smart.Begin(point, 6);
        smart.End(new(point.X + 5, point.Y));
        Check(selection.Selection == refined, "The physical drag threshold preserves click behavior at 150 percent scale");

        var oldQuery = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRegion = new PixelRect(300, 100, 200, 180);
        var otherPoint = new PixelPoint(350, 150);
        var racedSelection = new SelectionSession(bounds);
        using var racedEditor = new CaptureEditSession(racedSelection);
        var racedDetector = new RegionFixture(p => p.X < 0 ? region : otherRegion,
            (p, _) => p.X < 0 ? oldQuery.Task : Task.FromResult<PixelRect?>(otherRegion));
        using var raced = new SmartSelectionSession(racedEditor, racedDetector);
        raced.Hover(point); await Task.Delay(100);
        raced.Hover(otherPoint); await Task.Delay(140);
        oldQuery.SetResult(refined); await Task.Delay(80);
        Check(raced.Preview == otherRegion, "A late response from a previous window cannot overwrite the current hover target");
        raced.Dispose(); await Task.Delay(50);
        Check(raced.Preview.IsEmpty, "Closing the capture session removes its preview and cancels background updates");

        var clippedSelection = new SelectionSession(bounds);
        using var clippedEditor = new CaptureEditSession(clippedSelection);
        var oversized = new PixelRect(-2200, -100, 4600, 1400);
        using var clipped = new SmartSelectionSession(clippedEditor,
            new RegionFixture(_ => oversized, (_, _) => Task.FromResult<PixelRect?>(oversized)));
        clipped.Hover(point); await Task.Delay(120); clipped.Begin(point, 4); clipped.End(point);
        Check(clippedSelection.Selection == bounds, "Detected regions are clipped to the captured desktop, including negative origins");
    }
}
