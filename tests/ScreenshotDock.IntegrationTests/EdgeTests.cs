using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static async Task RunEdgeTestsAsync()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var triggerWidth = monitor.Bounds.Width / 3;
        var triggerLeft = monitor.Bounds.X + (monitor.Bounds.Width - triggerWidth) / 2;
        var triggerRight = triggerLeft + triggerWidth;
        var triggerCenter = monitor.Bounds.X + monitor.Bounds.Width / 2;
        var pointer = new PixelPoint(monitor.Bounds.X + 40, monitor.Bounds.Y + 40);
        var pressed = false;
        var count = 0;
        var foreground = GetForegroundWindow();
        var fixture = new Window { Title = "ScreenshotDock edge fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowActivated = false, Topmost = true,
            Background = Brushes.White, Width = monitor.Bounds.Width / monitor.Scale, Height = 20 };
        DesktopEnvironment.DoNotActivate(fixture);
        fixture.Show();
        DesktopEnvironment.Place(fixture, new(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, (int)(20 * monitor.Scale)));
        var edge = new EdgeWindow(() => pointer, () => pressed);
        edge.ExpandRequested += () => count++;
        try
        {
            edge.Show();
            await Task.Delay(80);
            Check(GetWindowRect(new WindowInteropHelper(edge).Handle, out var bounds)
                && bounds.Left == triggerLeft && bounds.Top == monitor.Bounds.Y && bounds.Right == triggerRight
                && bounds.Bottom - bounds.Top == Math.Max(2, (int)Math.Ceiling(4 * monitor.Scale)),
                "Invisible trigger occupies the centered third of the primary screen at four DIP high");
            File.WriteAllText(Path.Combine(_output, "trigger-bounds.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                Monitor = monitor.Bounds, monitor.Scale,
                Trigger = new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                LeftMargin = bounds.Left - monitor.Bounds.X, RightMargin = monitor.Bounds.Right - bounds.Right
            }));
            var visual = SaveVisual(edge, "invisible-top-edge.png");
            var pixels = new byte[visual.PixelWidth * visual.PixelHeight * 4];
            new FormatConvertedBitmap(visual, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, visual.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).All(i => pixels[i * 4 + 3] == 0), "Top trigger is fully transparent without a colored tab");
            Check(GetForegroundWindow() == foreground, "Showing the invisible trigger does not take keyboard focus");
            foreach (var x in new[] { monitor.Bounds.X + 1, monitor.Bounds.X + monitor.Bounds.Width / 2, monitor.Bounds.Right - 1 })
                Check(WindowFromPoint(new NativePoint { X = x, Y = monitor.Bounds.Y + 1 }) == new WindowInteropHelper(fixture).Handle,
                    "Invisible top edge leaves the underlying window as the native mouse target: " + x);

            foreach (var x in new[] { monitor.Bounds.X + 1, triggerLeft - 1, triggerRight, monitor.Bounds.Right - 1 })
            {
                var before = count;
                pointer = new(x, monitor.Bounds.Y + 1); await Task.Delay(300);
                Check(count == before, "Hover outside the centered trigger leaves the shelf collapsed: " + x);
            }
            foreach (var x in new[] { triggerLeft, triggerCenter, triggerRight - 1 })
            {
                pointer = new(x, monitor.Bounds.Y + 40); await Task.Delay(80);
                var before = count;
                pointer = new(x, monitor.Bounds.Y + 1); await Task.Delay(300);
                Check(count == before + 1, "Hover opens throughout the centered trigger, including its boundary pixels: " + x);
            }
            var heldCount = count;
            await Task.Delay(300);
            Check(count == heldCount, "Staying in the top edge emits only one open request");
            pointer = new(triggerCenter, monitor.Bounds.Y + 40); await Task.Delay(80);
            pointer = new(triggerCenter, monitor.Bounds.Y + 1); await Task.Delay(60);
            pointer = new(triggerCenter, monitor.Bounds.Y + 40); await Task.Delay(260);
            Check(count == heldCount, "A quick pass across the edge cancels the dwell and does not open");
            pointer = new(triggerCenter, monitor.Bounds.Y + 1); await Task.Delay(60);
            pointer = new(triggerRight, monitor.Bounds.Y + 1); await Task.Delay(300);
            Check(count == heldCount, "Moving sideways out of the centered trigger cancels the pending dwell");
            pointer = new(triggerCenter, monitor.Bounds.Y + 1); await Task.Delay(60);
            Check(count == heldCount, "Returning from a side region starts a fresh dwell instead of opening immediately");
            await Task.Delay(240);
            Check(count == heldCount + 1, "A new full dwell after returning from the side opens the shelf");
            heldCount = count;
            pointer = new(monitor.Bounds.Right, monitor.Bounds.Y + 1); await Task.Delay(300);
            Check(count == heldCount, "Locations beyond the primary screen do not trigger its shelf");
            pressed = true; pointer = new(triggerCenter, monitor.Bounds.Y + 1); await Task.Delay(300);
            Check(count == heldCount, "Holding a mouse button prevents hover opening during a drag");
            pressed = false; await Task.Delay(300);
            Check(count == heldCount + 1, "The top edge resumes hover detection after releasing the mouse button");
            edge.Hide(); heldCount = count; await Task.Delay(300);
            Check(count == heldCount, "Hidden top edge stops hover detection");
            edge.Show(); await Task.Delay(300);
            Check(count == heldCount + 1, "Showing the top edge restarts its hover detection");
        }
        finally { edge.Close(); fixture.Close(); }
        var afterClose = count;
        await Task.Delay(260);
        Check(count == afterClose, "Closing the trigger stops its timer and pending hover");

        pointer = new(monitor.Bounds.X + 40, monitor.Bounds.Y + 40);
        var root = Path.Combine(_output, "app-data");
        new SettingsStore(root).Save(new() { AutoCopy = false, AutoExpand = false,
            PinHotkey = TestShortcut(0x7A),
            Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x77, KeyName = "F8" } });
        using var controller = new AppController(root, edgeFactory: () => new EdgeWindow(() => pointer, () => pressed));
        try
        {
            await controller.StartAsync();
            var dock = _application.Windows.OfType<DockWindow>().Single();
            var appEdge = _application.Windows.OfType<EdgeWindow>().Single();
            dock.RequestCollapse();
            await Task.Delay(400);
            Check(!dock.IsVisible && appEdge.IsVisible, "Manual retraction returns to the invisible centered trigger");
            pointer = new(monitor.Bounds.X + 1, monitor.Bounds.Y + 1);
            await Task.Delay(300);
            Check(!dock.IsVisible && appEdge.IsVisible, "Top-left hover leaves the actual application shelf collapsed");
            pointer = new(monitor.Bounds.Right - 1, monitor.Bounds.Y + 1);
            await Task.Delay(300);
            Check(!dock.IsVisible && appEdge.IsVisible, "Top-right hover leaves the actual application shelf collapsed");
            pointer = new(triggerCenter, monitor.Bounds.Y + 1);
            await Task.Delay(300);
            Check(dock.IsVisible && !appEdge.IsVisible, "Top-center hover expands the actual application shelf and stops the trigger");
            Check(GetWindowRect(new WindowInteropHelper(dock).Handle, out var dockBounds)
                && dockBounds.Left == monitor.Bounds.X && dockBounds.Right == monitor.Bounds.Right,
                "The expanded shelf still covers the full primary-screen width");
            dock.SetLocked(true);
            var capture = controller.CaptureAsync();
            for (var i = 0; i < 100 && !_application.Windows.OfType<ScreenshotDock.App.Views.CaptureWindow>().Any(); i++) await Task.Delay(10);
            await Task.Delay(300);
            Check(_application.Windows.OfType<CaptureWindow>().Any() && !dock.IsVisible && !appEdge.IsVisible,
                "Entering capture suspends the invisible trigger even while the pointer remains at the top");
            ClickEditor(_application.Windows.OfType<CaptureWindow>().First(), "CancelCaptureButton");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            pointer = new(monitor.Bounds.X + 40, monitor.Bounds.Y + 40);
            dock.RequestCollapse();
            await Task.Delay(400);
            Check(!dock.IsVisible && appEdge.IsVisible, "Canceling capture and retracting restores the invisible hover trigger");
        }
        finally { controller.Dispose(); SetForegroundWindow(foreground); }
        await RenderThemePreviewAsync();
        Check(_application.Windows.Count == 0, "Application exit closes the trigger, shelf, shadow and capture windows");
        Console.WriteLine($"{Checks.Count} invisible top-edge checks passed.");
    }
}
