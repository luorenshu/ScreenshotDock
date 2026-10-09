using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App;
using ScreenshotDock.App.ViewModels;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Clipboard;
using ScreenshotDock.Windows.Hotkeys;
using ScreenshotDock.Windows.Native;
using ScreenshotDock.Windows.Tray;
using Forms = System.Windows.Forms;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static readonly List<object> Checks = [];
    private static string _output = "";
    private static Application _application = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--drop-target") { RunDropTarget(args[1]); return 0; }
        if (args.FirstOrDefault() == "--wheel-target") { RunWheelTarget(args[1]); return 0; }
        if (args.FirstOrDefault() == "--fullscreen-target") { RunFullscreenTarget(); return 0; }
        var editorOnly = args.FirstOrDefault() == "--editor-tests";
        var pinOnly = args.FirstOrDefault() == "--pin-tests";
        var edgeOnly = args.FirstOrDefault() == "--edge-tests";
        var settingsOnly = args.FirstOrDefault() == "--settings-tests";
        var annotationsOnly = args.FirstOrDefault() == "--annotation-tests";
        _output = Path.GetFullPath((editorOnly || pinOnly || edgeOnly || settingsOnly || annotationsOnly ? args.ElementAtOrDefault(1) : args.FirstOrDefault()) ?? "artifacts/verification");
        Directory.CreateDirectory(_output);
        _application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenshotDock;component/Themes/Theme.xaml") });
        _application.Dispatcher.BeginInvoke(async () =>
        {
            try { if (annotationsOnly) await RunAnnotationFeatureTestsAsync(); else if (settingsOnly) await RunSettingsTestsAsync(); else if (edgeOnly) await RunEdgeTestsAsync(); else if (pinOnly) await RunPinTestsAsync(); else if (editorOnly) await RunEditorTestsAsync(); else await RunAsync(); File.WriteAllText(Path.Combine(_output, "integration-results.json"), JsonSerializer.Serialize(Checks, AtomicJson.Options)); File.Delete(Path.Combine(_output, "failure.txt")); _application.Shutdown(0); }
            catch (Exception e) { Console.Error.WriteLine(e); File.WriteAllText(Path.Combine(_output, "integration-results.json"), JsonSerializer.Serialize(Checks, AtomicJson.Options)); File.WriteAllText(Path.Combine(_output, "failure.txt"), e.ToString()); _application.Shutdown(1); }
        });
        return _application.Run();
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException(name);
        Checks.Add(new { name, status = "passed" });
        Console.WriteLine($"PASS: {name}");
    }

    private static async Task RunAsync()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        File.WriteAllText(Path.Combine(_output, "desktop-environment.json"), JsonSerializer.Serialize(new
        {
            virtualBounds = DesktopEnvironment.VirtualBounds,
            monitors = DesktopEnvironment.Monitors.Select(m => new { m.Bounds, m.WorkArea, m.Scale, m.IsPrimary })
        }, AtomicJson.Options));
        Check(DesktopEnvironment.VirtualBounds.Width > 0 && monitor.Scale > 0, "Physical monitor enumeration");
        var green = Color.FromRgb(40, 120, 75);
        var testWindow = new Window { Title = "ScreenshotDock integration fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(green), Topmost = true, Width = 440, Height = 220,
            Content = new TextBlock { Text = "Capture. Keep.\nDrag. Share.", FontSize = 38, Foreground = Brushes.White, Margin = new Thickness(28), FontWeight = FontWeights.SemiBold } };
        var rendered = new TaskCompletionSource();
        testWindow.ContentRendered += (_, _) => rendered.TrySetResult();
        testWindow.Show();
        var fixtureBounds = new PixelRect(monitor.WorkArea.X + 120, monitor.WorkArea.Y + 340, 440, 220);
        DesktopEnvironment.Place(testWindow, fixtureBounds);
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        DesktopEnvironment.FlushComposition();
        var frame = ScreenCaptureService.CaptureDesktop();
        var selected = ScreenCaptureService.Crop(frame, fixtureBounds);
        Check(selected.PixelWidth == 440 && selected.PixelHeight == 220, "GDI crop preserves physical pixel dimensions");
        var sample = new FormatConvertedBitmap(selected, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        sample.CopyPixels(new Int32Rect(3, 3, 1, 1), pixel, 4, 0);
        File.WriteAllBytes(Path.Combine(_output, "captured-region.png"), ScreenCaptureService.EncodePng(selected));
        Check(pixel[0] == green.B && pixel[1] == green.G && pixel[2] == green.R, $"BitBlt captures actual desktop pixels (RGB {pixel[2]},{pixel[1]},{pixel[0]})");

        var root = Path.Combine(_output, "isolated-cache");
        var repository = new ScreenshotRepository(root);
        repository.Load(new(), DateTimeOffset.UtcNow);
        repository.Clear();
        var added = repository.Add(ScreenCaptureService.EncodePng(selected), ScreenCaptureService.Thumbnail(selected), new(), DateTimeOffset.UtcNow);
        var card = new ScreenshotCard(added.Item, repository);
        await ImageTransferService.CopyAsync(selected);
        Check(System.Windows.Clipboard.ContainsImage(), "Clipboard accepts image");
        var clipboardImage = System.Windows.Clipboard.GetImage();
        Check(clipboardImage?.PixelWidth == 440 && clipboardImage.PixelHeight == 220, "Clipboard image retains original dimensions");
        var data = ImageTransferService.CreateDragData(card.FilePath);
        Check(data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && File.Exists(paths[0]), "Drag payload contains a real PNG file");

        await TestHotkeysAsync();
        await TestSingleKeyAsync();
        await TestTrayAsync();
        TestSelection();
        Check(DesktopEnvironment.CoversMonitor(new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1080)), "Fullscreen classifier covers secondary monitor");
        Check(!DesktopEnvironment.CoversMonitor(new(0, 0, 1920, 1040), new(0, 0, 1920, 1080)), "Maximized work area is distinct from fullscreen");

        var session = new SelectionSession(frame.Bounds);
        session.SetSelection(fixtureBounds);
        var overlay = new CaptureWindow(frame, monitor, session);
        var completed = false;
        var canceled = false;
        overlay.CompleteRequested += () => completed = true;
        overlay.CancelRequested += () => canceled = true;
        overlay.Show();
        overlay.Activate();
        await Task.Delay(200);
        SaveVisual(overlay, "capture-overlay.png");
        KeyPress(0x0D);
        await Task.Delay(100);
        Check(completed, "Capture overlay receives Enter completion");
        KeyPress(0x1B);
        await Task.Delay(100);
        Check(canceled, "Capture overlay receives Escape cancellation");
        overlay.Close();

        // Additional real cards exercise horizontal layout and image aspect ratios.
        var more = new List<ScreenshotCard> { card };
        foreach (var spec in new[] { (600, 360, "工作台 / Workspace", "#D5E2CF"), (340, 520, "产品笔记 / Notes", "#EDE6D5"), (560, 320, "终端 / Terminal", "#233C31") })
        {
            var image = CreateIllustration(spec.Item1, spec.Item2, spec.Item3, spec.Item4);
            var item = repository.Add(ScreenCaptureService.EncodePng(image), ScreenCaptureService.Thumbnail(image), new(), DateTimeOffset.UtcNow);
            more.Add(new(item.Item, repository));
        }
        testWindow.Close();
        var dock = new DockWindow();
        dock.Configure(new());
        dock.SetCards(more.Take(3));
        dock.SetLocked(true);
        dock.Expand(TimeSpan.FromMinutes(1));
        await Task.Delay(120);
        SaveVisual(dock, "dock-dropping.png");
        var backdrop = _application.Windows.OfType<DockBackdropWindow>().Single();
        var shadeDuring = backdrop.ShadeOpacity;
        var dropTransform = (TranslateTransform)((Border)dock.FindName("DockContent")).RenderTransform;
        var dropDuring = dropTransform.Y;
        await Task.Delay(380);
        File.WriteAllText(Path.Combine(_output, "entrance-animation.json"), JsonSerializer.Serialize(new
        {
            systemAnimationsEnabled = SystemParameters.ClientAreaAnimation,
            duringDropY = dropDuring,
            restingY = dropTransform.Y,
            restingOpacity = ((Border)dock.FindName("DockContent")).Opacity,
            duringShadeOpacity = shadeDuring,
            restingShadeOpacity = backdrop.ShadeOpacity
        }, AtomicJson.Options));
        dock.ShowCopied(more[1].Item.Id);
        await Task.Delay(80);
        var dockVisual = SaveVisual(dock, "dock.png");
        var shadeVisual = SaveVisual(backdrop, "dock-shadow.png");
        SaveShelfComposition(dockVisual, shadeVisual, "dock-composited.png");
        var dockHandle = new System.Windows.Interop.WindowInteropHelper(dock).Handle;
        Check(GetWindowRect(dockHandle, out var dockBounds) && dockBounds.Left == monitor.Bounds.X && dockBounds.Top == monitor.Bounds.Y && dockBounds.Right == monitor.Bounds.Right,
            "Expanded shelf spans the full physical monitor width");
        var dockDpi = VisualTreeHelper.GetDpi(dock);
        var blankPixel = new byte[4];
        dockVisual.CopyPixels(new Int32Rect((int)(30 * dockDpi.DpiScaleX), (int)(285 * dockDpi.DpiScaleY), 1, 1), blankPixel, 4, 0);
        Check(blankPixel[3] == 0, "Screenshot card gaps remain transparent above the separate shadow layer");
        await TestDockClickThroughAsync(dock, monitor);
        dock.SetCards(more);
        Check(dock.ActualWidth > 500 && dock.ActualHeight > 200, "Dock opens at usable dimensions");
        await TestOleDragAsync(dock, repository.GetImagePath(card.Item.Id), monitor);
        await TestDockWheelAsync(dock, more, repository);
        await TestDockRetractionAsync(dock);
        dock.Close();

        var emptyDock = new DockWindow();
        emptyDock.Configure(new());
        emptyDock.SetCards([]);
        emptyDock.SetLocked(true);
        emptyDock.Expand(TimeSpan.FromMinutes(1));
        await Task.Delay(500);
        SaveVisual(emptyDock, "dock-empty.png");
        emptyDock.Close();

        var settings = new SettingsWindow(new(), root, _ => Task.FromResult<string?>(null));
        settings.Show();
        await Task.Delay(150);
        SaveVisual(settings, "settings.png");
        settings.Close();
        await TestSingleKeySettingsAsync(root);
        var preview = new PreviewWindow(card);
        preview.Show();
        await Task.Delay(100);
        SaveVisual(preview, "preview.png");
        preview.Close();
        Check(_application.Windows.Count == 0, "Closing tool windows leaves no stray overlays");
        await TestApplicationLifecycleAsync(monitor);
        Console.WriteLine($"{Checks.Count} integration checks passed.");
    }

    private static async Task TestHotkeysAsync()
    {
        using var first = new GlobalHotkeyService();
        using var second = new GlobalHotkeyService();
        using var third = new GlobalHotkeyService();
        var a = new HotkeySettings { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x79, KeyName = "F10" };
        var b = a with { VirtualKey = 0x78, KeyName = "F9" };
        Check(first.TryRegister(a, out _), "Register global hotkey");
        Check(!second.TryRegister(a, out var error) && error.Contains("占用"), "Conflicting global hotkey reports error");
        Check(second.TryRegister(b, out _), "Register replacement candidate");
        Check(!second.TryRegister(a, out _) && !third.TryRegister(b, out _), "Failed replacement preserves old shortcut");
        var invoked = false;
        first.Pressed += (_, _) => invoked = true;
        KeyDown(0x11); KeyDown(0x12); KeyDown(0x10); KeyPress(0x79); KeyUp(0x10); KeyUp(0x12); KeyUp(0x11);
        await Task.Delay(120);
        Check(invoked, "Actual keyboard input dispatches WM_HOTKEY");
        Check(first.SetPaused(true, out _) && third.TryRegister(a, out _), "Pause releases shortcut to other apps");
        Check(!first.SetPaused(false, out _) && first.IsPaused, "Resume conflict keeps listener paused");
        third.SetPaused(true, out _);
        Check(first.SetPaused(false, out _) && !first.IsPaused, "Resume succeeds after conflict is released");
    }

    private static async Task TestTrayAsync()
    {
        using var icon = typeof(AppController).Assembly.GetManifestResourceStream("ScreenshotDock.AppIcon.ico")!;
        using var tray = new TrayService(icon);
        var opened = false;
        tray.DockRequested += () => opened = true;
        PostMessage(tray.Handle, 0x8001, 1, 0x0203);
        await Task.Delay(80);
        Check(opened, "Native tray double-click dispatches dock action");
        opened = false;
        var menuEvents = new List<object>();
        nint menu = 0;
        using var menuReady = new System.Threading.ManualResetEventSlim();
        var source = System.Windows.Interop.HwndSource.FromHwnd(tray.Handle);
        source.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message is 0x0211 or 0x0212 or 0x0117 or 0x011F)
            {
                menuEvents.Add(new { message, wParam = wParam.ToInt64(), cursor = DesktopEnvironment.Cursor });
                if (message == 0x0117) { menu = wParam; menuReady.Set(); }
            }
            return 0;
        });
        tray.CaptureRequested += () => menuEvents.Add(new { action = "capture" });
        tray.SettingsRequested += () => menuEvents.Add(new { action = "settings" });
        var trayHandle = tray.Handle;
        var input = Task.Run(async () =>
        {
            var ready = menuReady.Wait(TimeSpan.FromSeconds(2));
            var item = new NativeRect();
            var located = false;
            for (var attempt = 0; attempt < 30 && !located; attempt++)
            {
                await Task.Delay(50);
                located = ready && GetMenuItemRect(0, menu, 0, out item) && item.Right > item.Left && item.Bottom > item.Top;
            }
            if (!located) { KeyPress(0x1B); throw new InvalidOperationException("Native tray menu item has no screen bounds."); }
            var before = ScreenCaptureService.CaptureDesktop();
            File.WriteAllBytes(Path.Combine(_output, "tray-menu-item.png"), ScreenCaptureService.EncodePng(ScreenCaptureService.Crop(before, new PixelRect(item.Left, item.Top, item.Right - item.Left, item.Bottom - item.Top))));
            File.WriteAllText(Path.Combine(_output, "tray-input.json"), JsonSerializer.Serialize(new
            {
                ready,
                foreground = GetForegroundWindow().ToInt64(),
                tray = trayHandle.ToInt64(),
                cursor = DesktopEnvironment.Cursor,
                item = new { item.Left, item.Top, item.Right, item.Bottom },
                control = GetAsyncKeyState(0x11), alt = GetAsyncKeyState(0x12), shift = GetAsyncKeyState(0x10)
            }, AtomicJson.Options));
            SetCursorPos((item.Left + item.Right) / 2, (item.Top + item.Bottom) / 2);
            await Task.Delay(60);
            Mouse(0x0002);
            await Task.Delay(60);
            Mouse(0x0004);
        });
        PostMessage(tray.Handle, 0x8001, 1, 0x0205);
        await input;
        await Task.Delay(100);
        File.WriteAllText(Path.Combine(_output, "tray-menu-events.json"), JsonSerializer.Serialize(menuEvents, AtomicJson.Options));
        Check(opened, "Native tray context menu dispatches selected action");
    }

    private static async Task TestSingleKeyAsync()
    {
        using var listener = new GlobalHotkeyService();
        using var competitor = new GlobalHotkeyService();
        HotkeySettings? registered = null;
        for (uint key = 0x70; key <= 0x7A; key++)
        {
            var candidate = new HotkeySettings { Modifiers = HotkeyModifiers.None, VirtualKey = key, KeyName = $"F{key - 0x6F}" };
            if (listener.TryRegister(candidate, out _)) { registered = candidate; break; }
        }
        Check(registered is not null, "Register standalone function key without modifiers");
        Check(!competitor.TryRegister(registered!, out var error) && error.Contains("占用"), "Standalone function key detects native conflict");
        var invoked = false;
        listener.Pressed += (_, _) => invoked = true;
        KeyPress((ushort)registered!.VirtualKey);
        await Task.Delay(100);
        Check(invoked, "Actual standalone function key dispatches screenshot shortcut");
    }

    private static async Task TestSingleKeySettingsAsync(string root)
    {
        AppSettings? saved = null;
        var settings = new SettingsWindow(new(), root, value => { saved = value; return Task.FromResult<string?>(null); });
        settings.Show();
        await Task.Delay(100);
        var box = (TextBox)settings.FindName("HotkeyBox");
        box.Focus();
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(settings)!, Environment.TickCount, Key.F2)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(box.Text == "F2", "Settings input records standalone F2");
        ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(80);
        Check(saved?.Hotkey.Modifiers == HotkeyModifiers.None && saved.Hotkey.VirtualKey == 0x71, "Settings save preserves standalone F2 without adding modifiers");
        if (settings.IsVisible) settings.Close();
    }

    private static async Task TestApplicationLifecycleAsync(MonitorDescriptor monitor)
    {
        var root = Path.Combine(_output, "isolated-app-data");
        var cache = new ScreenshotRepository(root);
        cache.Load(new(), DateTimeOffset.UtcNow);
        cache.Clear();
        var shortcut = new HotkeySettings { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x77, KeyName = "F8" };
        new SettingsStore(root).Save(new() { Hotkey = shortcut });
        using var controller = new AppController(root);
        await controller.StartAsync();
        Check(_application.Windows.OfType<DockWindow>().Any() && _application.Windows.OfType<EdgeWindow>().Any(), "Application startup creates dock and edge trigger");
        using var competitor = new GlobalHotkeyService();
        Check(!_application.Windows.OfType<SettingsWindow>().Any() && !competitor.TryRegister(shortcut, out _), "Running app owns configured screenshot shortcut");

        var bounds = new PixelRect(monitor.WorkArea.X + 100, monitor.WorkArea.Y + 370, 400, 220);
        var fixture = new Window { Title = "ScreenshotDock capture pipeline fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(40, 120, 75)), Topmost = true, Width = 400, Height = 220 };
        fixture.Show();
        DesktopEnvironment.Place(fixture, bounds);
        await Task.Delay(150);
        var capture = controller.CaptureAsync();
        for (var i = 0; i < 50 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(20);
        Check(_application.Windows.OfType<CaptureWindow>().Any(), "Full app launches capture overlays");
        Check(!_application.Windows.OfType<DockWindow>().Single().IsVisible && !_application.Windows.OfType<EdgeWindow>().Single().IsVisible && !_application.Windows.OfType<DockBackdropWindow>().Single().IsVisible,
            "Capture hides app shelf, gradient shadow and edge trigger before sampling");
        await _application.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(150);
        DesktopEnvironment.FlushComposition();
        var captureInput = new List<object>();
        var captureOverlay = _application.Windows.OfType<CaptureWindow>().First();
        captureOverlay.PreviewMouseLeftButtonDown += (_, e) => captureInput.Add(new { action = "received-down", cursor = DesktopEnvironment.Cursor, screen = captureOverlay.PointToScreen(e.GetPosition(captureOverlay)) });
        captureOverlay.PreviewMouseLeftButtonUp += (_, e) => captureInput.Add(new { action = "received-up", cursor = DesktopEnvironment.Cursor, screen = captureOverlay.PointToScreen(e.GetPosition(captureOverlay)) });
        var left = bounds.X + 10;
        var top = bounds.Y + 10;
        SetCursorPos(left, top);
        await Task.Delay(50);
        captureInput.Add(new { action = "send-down", cursor = DesktopEnvironment.Cursor });
        Mouse(0x0002);
        await Task.Delay(80);
        SetCursorPos(left + 300, top + 160);
        await Task.Delay(100);
        captureInput.Add(new { action = "send-up", cursor = DesktopEnvironment.Cursor });
        Mouse(0x0004);
        await Task.Delay(80);
        KeyPress(0x0D);
        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        cache.Load(new(), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_output, "capture-input.json"), JsonSerializer.Serialize(captureInput, AtomicJson.Options));
        Check(cache.Items.Count == 1 && cache.Items[0].Width == 300 && cache.Items[0].Height == 160, $"Mouse region capture saves PNG through full app pipeline (received {string.Join(", ", cache.Items.Select(item => $"{item.Width}x{item.Height}"))})");
        Check(System.Windows.Clipboard.GetImage()?.PixelWidth == 300, "Full capture pipeline automatically copies image");
        Check(!_application.Windows.OfType<CaptureWindow>().Any(), "Capture removes every overlay after completion");
        fixture.Close();
        var dock = _application.Windows.OfType<DockWindow>().Single();
        var settingsButton = Descendants(dock).OfType<Button>().Single(b => b.ToolTip as string == "设置");
        settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(_application.Windows.OfType<SettingsWindow>().Any(), "Dock opens actual settings window");
        _application.Windows.OfType<SettingsWindow>().Single().Close();
        await Task.Delay(500);
        dock.RequestCollapse();
        await Task.Delay(500);
        Check(!dock.IsVisible && _application.Windows.OfType<EdgeWindow>().Single().IsVisible, "Manual retraction hides the shelf and restores the top trigger");
        var canceled = controller.CaptureAsync();
        for (var i = 0; i < 50 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(20);
        KeyPress(0x1B);
        await canceled.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Load(new(), DateTimeOffset.UtcNow);
        Check(cache.Items.Count == 1, "Escape cancels full app capture without writing a new image");
        Check(_application.Windows.OfType<EdgeWindow>().Any(), "App remains active after settings closes");
        await TestFullscreenAndHoverAsync(dock, monitor);
        controller.Dispose();
        var released = competitor.TryRegister(shortcut, out var exitError);
        Check(released, $"App exit releases global shortcut{(released ? "" : ": " + exitError)}");
        Check(_application.Windows.Count == 0, "App exit disposes all tool windows");
    }

    private static async Task TestFullscreenAndHoverAsync(DockWindow dock, MonitorDescriptor monitor)
    {
        dock.SetLocked(true);
        dock.Expand(TimeSpan.Zero);
        using var fullscreen = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, Arguments = "--fullscreen-target" })!;
        try
        {
            for (var i = 0; i < 50 && fullscreen.MainWindowHandle == 0; i++) { await Task.Delay(100); fullscreen.Refresh(); }
            Check(fullscreen.MainWindowHandle != 0, "Independent fullscreen test window starts");
            SetCursorPos(monitor.Bounds.X + monitor.Bounds.Width / 2, monitor.Bounds.Y + monitor.Bounds.Height / 2);
            Mouse(0x0002); Mouse(0x0004);
            await Task.Delay(200);
            Check(DesktopEnvironment.IsFullscreenForeground(), "Actual external fullscreen window is detected");
            await Task.Delay(2200);
            Check(!dock.IsVisible && !_application.Windows.OfType<EdgeWindow>().Single().IsVisible && !_application.Windows.OfType<DockBackdropWindow>().Single().IsVisible,
                "Fullscreen suppresses shelf, gradient shadow and hover trigger");
        }
        finally { if (!fullscreen.HasExited) { fullscreen.Kill(); await fullscreen.WaitForExitAsync(); } }
        await Task.Delay(2200);
        Check(dock.IsVisible, "Locked shelf is restored after fullscreen exits");
        dock.SetLocked(false);
        dock.Expand(TimeSpan.Zero);
        await Task.Delay(1700);
        var edge = _application.Windows.OfType<EdgeWindow>().Single();
        Check(!dock.IsVisible && edge.IsVisible, "Shelf collapses after pointer leaves");
        var trigger = edge.PointToScreen(new Point(edge.ActualWidth / 2, edge.ActualHeight / 2));
        SetCursorPos((int)trigger.X, (int)trigger.Y);
        await Task.Delay(500);
        Check(dock.IsVisible && !edge.IsVisible, "Actual top-edge mouse hover expands shelf");
        await TestHotAreaExitAsync(dock, edge);
    }

    private static async Task TestHotAreaExitAsync(DockWindow dock, EdgeWindow edge)
    {
        dock.Expand(TimeSpan.FromSeconds(30));
        var card = Descendants(dock).OfType<Border>().First(b => b.Tag is ScreenshotCard);
        var onCard = card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2));
        SetCursorPos((int)onCard.X, (int)onCard.Y);
        await Task.Delay(150);
        var blank = dock.PointToScreen(new Point(30, dock.ActualHeight - 40));
        SetCursorPos((int)blank.X, (int)blank.Y);
        var hoverInput = new List<object>();
        for (var sample = 0; sample < 46; sample++)
        {
            await Task.Delay(50);
            hoverInput.Add(new { sample, expected = blank, cursor = DesktopEnvironment.Cursor, visible = dock.IsVisible, locked = dock.IsLocked });
        }
        File.WriteAllText(Path.Combine(_output, "hot-area-input.json"), JsonSerializer.Serialize(hoverInput, AtomicJson.Options));
        Check(dock.IsVisible, "Moving from a card into transparent hot-area space keeps the shelf open");

        var outside = dock.PointToScreen(new Point(30, dock.ActualHeight + 40));
        SetCursorPos((int)outside.X, (int)outside.Y);
        await Task.Delay(650);
        Check(!dock.IsVisible && edge.IsVisible, "Leaving the physical hot area retracts the shelf without waiting for the preview grace period");

        if (!SystemParameters.ClientAreaAnimation) return;
        var trigger = edge.PointToScreen(new Point(edge.ActualWidth / 2, edge.ActualHeight / 2));
        SetCursorPos((int)trigger.X, (int)trigger.Y);
        await Task.Delay(500);
        dock.Expand(TimeSpan.FromSeconds(30));
        SetCursorPos((int)blank.X, (int)blank.Y);
        await Task.Delay(500);
        SetCursorPos((int)outside.X, (int)outside.Y);
        var retract = dock.CollapseAsync(cancelOnPointerReturn: true);
        await Task.Delay(60);
        SetCursorPos((int)blank.X, (int)blank.Y);
        var resumed = !await retract.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(500);
        Check(resumed && dock.IsVisible && !edge.IsVisible, "Returning to transparent hot-area space interrupts automatic retraction");
    }

    private static void RunFullscreenTarget()
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        var bounds = DesktopEnvironment.PrimaryMonitor.Bounds;
        using var form = new Forms.Form { Text = "ScreenshotDock fullscreen integration fixture", FormBorderStyle = Forms.FormBorderStyle.None,
            StartPosition = Forms.FormStartPosition.Manual, Bounds = new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            BackColor = System.Drawing.Color.FromArgb(30, 45, 35), TopMost = true };
        form.Controls.Add(new Forms.Label { Text = "Fullscreen detection check", AutoSize = true, ForeColor = System.Drawing.Color.White, Location = new System.Drawing.Point(50, 50) });
        Forms.Application.Run(form);
    }

    private static void TestSelection()
    {
        var selection = new SelectionSession(new(-1920, -200, 3840, 1280));
        selection.Begin(new(-1700, 0), 7); selection.Update(new(-1200, 300)); selection.End();
        Check(selection.Selection == new PixelRect(-1700, 0, 500, 300), "Selection retains negative physical origin");
        selection.Begin(new(-1400, 150), 7); selection.Update(new(-1300, 200)); selection.End();
        Check(selection.Selection == new PixelRect(-1600, 50, 500, 300), "Selection dragging preserves size");
        selection.Begin(new(-1100, 350), 7); selection.Update(new(-1000, 400)); selection.End();
        Check(selection.Selection == new PixelRect(-1600, 50, 600, 350), "Selection corner resize adjusts dimensions");
    }

    private static async Task TestOleDragAsync(DockWindow dock, string sourcePath, MonitorDescriptor monitor)
    {
        var receiverDirectory = Path.Combine(_output, "drop-receiver");
        Directory.CreateDirectory(receiverDirectory);
        var resultPath = Path.Combine(receiverDirectory, "received.png");
        if (File.Exists(resultPath)) File.Delete(resultPath);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--drop-target");
        info.ArgumentList.Add(receiverDirectory);
        using var receiver = Process.Start(info)!;
        try
        {
            for (var i = 0; i < 60 && receiver.MainWindowHandle == 0; i++) { await Task.Delay(100); receiver.Refresh(); }
            Check(receiver.MainWindowHandle != 0, "Independent OLE receiver process starts");
            var surface = Descendants(dock).OfType<Border>().First(b => b.Tag is ScreenshotCard card && card.FilePath == sourcePath);
            var source = surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            var destination = new Point(monitor.WorkArea.X + 675, monitor.WorkArea.Y + 525);
            var input = Task.Run(async () =>
            {
                SetCursorPos((int)source.X, (int)source.Y);
                await Task.Delay(100);
                Mouse(0x0002);
                await Task.Delay(100);
                for (var step = 1; step <= 25; step++)
                {
                    SetCursorPos((int)(source.X + (destination.X - source.X) * step / 25), (int)(source.Y + (destination.Y - source.Y) * step / 25));
                    await Task.Delay(30);
                }
                await Task.Delay(250);
                Mouse(0x0004);
            });
            await input;
            for (var i = 0; i < 30 && !File.Exists(resultPath); i++) await Task.Delay(100);
            Check(File.Exists(resultPath), "OLE drag from actual screenshot card reaches external process");
            Check(File.ReadAllBytes(resultPath).SequenceEqual(File.ReadAllBytes(sourcePath)), "External drop receives identical PNG bytes");
            Check(File.Exists(sourcePath), "Drag copy leaves cached original available");
        }
        finally { if (!receiver.HasExited) { receiver.Kill(); await receiver.WaitForExitAsync(); } }
    }

    private static async Task TestDockClickThroughAsync(DockWindow dock, MonitorDescriptor monitor)
    {
        var clicked = false;
        var button = new Button { Content = "Underlying window", Background = Brushes.Orange };
        button.Click += (_, _) => clicked = true;
        var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Width = 180, Height = 30, Content = button };
        try
        {
            fixture.Show();
            DesktopEnvironment.Place(fixture, new PixelRect(monitor.Bounds.X + 20, monitor.Bounds.Y + (int)(270 * monitor.Scale), 180, 30), topmost: false);
            dock.PositionAtTop();
            await Task.Delay(150);
            DesktopEnvironment.FlushComposition();
            SetCursorPos(monitor.Bounds.X + 50, monitor.Bounds.Y + (int)(285 * monitor.Scale));
            await Task.Delay(50);
            var target = WindowFromPoint(new NativePoint { X = monitor.Bounds.X + 50, Y = monitor.Bounds.Y + (int)(285 * monitor.Scale) });
            var beforeDown = DesktopEnvironment.Cursor;
            var downSent = Mouse(0x0002);
            await Task.Delay(50);
            var beforeUp = DesktopEnvironment.Cursor;
            var upSent = Mouse(0x0004);
            await Task.Delay(100);
            File.WriteAllText(Path.Combine(_output, "click-through.json"), JsonSerializer.Serialize(new
            {
                pointWindow = target.ToInt64(),
                underlyingWindow = new System.Windows.Interop.WindowInteropHelper(fixture).Handle.ToInt64(),
                shelfWindow = new System.Windows.Interop.WindowInteropHelper(dock).Handle.ToInt64(),
                beforeDown, beforeUp, downSent, upSent,
                clicked
            }, AtomicJson.Options));
            Check(clicked, "Transparent shelf space lets real mouse clicks reach the underlying window");
        }
        finally { fixture.Close(); }
    }

    private static async Task TestDockWheelAsync(DockWindow dock, List<ScreenshotCard> cards, ScreenshotRepository repository)
    {
        var receiverDirectory = Path.Combine(_output, "wheel-receiver");
        Directory.CreateDirectory(receiverDirectory);
        var countPath = Path.Combine(receiverDirectory, "count.txt");
        var clickPath = Path.Combine(receiverDirectory, "click.txt");
        File.WriteAllText(countPath, "0");
        File.WriteAllText(clickPath, "0");
        var observations = new List<object>();
        var scroller = (ScrollViewer)dock.FindName("CardScroller");
        int Received() => int.Parse(File.ReadAllText(countPath));
        void Observe(string step)
        {
            observations.Add(new { step, offset = scroller.HorizontalOffset, extent = scroller.ScrollableWidth, received = Received(), cursor = DesktopEnvironment.Cursor });
            File.WriteAllText(Path.Combine(_output, "wheel-routing.json"), JsonSerializer.Serialize(observations, AtomicJson.Options));
        }
        async Task ScrollAt(Point point, short delta, bool horizontal = false, int repeats = 1)
        {
            SetCursorPos((int)point.X, (int)point.Y);
            await Task.Delay(60);
            // Leave the hook's installing thread free to dispatch native input.
            await Task.Run(() => { for (var i = 0; i < repeats; i++) Wheel(delta, horizontal); });
            await Task.Delay(180);
        }
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--wheel-target");
        info.ArgumentList.Add(receiverDirectory);
        using var receiver = Process.Start(info)!;
        try
        {
            for (var i = 0; i < 60 && receiver.MainWindowHandle == 0; i++) { await Task.Delay(100); receiver.Refresh(); }
            Check(receiver.MainWindowHandle != 0, "Independent wheel receiver process starts");
            dock.SetCards(Enumerable.Range(0, 12).Select(i => new ScreenshotCard(cards[i % cards.Count].Item, repository)));
            dock.PositionAtTop();
            await Task.Delay(200);
            scroller.ScrollToHorizontalOffset(0);
            await Task.Delay(100);
            SaveVisual(dock, "dock-many.png");
            Check(scroller.ScrollableWidth > 600 && scroller.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed,
                "Overflowing screenshot cards remain scrollable with no visible scrollbar");
            var blank = dock.PointToScreen(new Point(50, 285));
            var outside = dock.PointToScreen(new Point(50, dock.ActualHeight + 100));
            SetCursorPos((int)blank.X, (int)blank.Y);
            await Task.Delay(60);
            Mouse(0x0002);
            await Task.Delay(60);
            Mouse(0x0004);
            await Task.Delay(120);
            Check(File.ReadAllText(clickPath) == "1", "Gradient shadow lets real clicks reach an independent underlying process");
            await ScrollAt(blank, -120);
            Observe("transparent blank");
            Check(Math.Abs(scroller.HorizontalOffset - 120) < 0.1, "Wheel over transparent hot-area space scrolls screenshot cards");
            Check(Received() == 0, "Hot-area wheel input does not reach the underlying process");

            var surface = Descendants(dock).OfType<Border>().First(b => b.Tag is ScreenshotCard && b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2)).X > 50);
            await ScrollAt(surface.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2)), -120);
            Observe("image surface");
            Check(Math.Abs(scroller.HorizontalOffset - 240) < 0.1 && Received() == 0, "Wheel over a screenshot scrolls exactly once");
            await ScrollAt(blank, -120, repeats: 2);
            Observe("rapid wheel");
            Check(Math.Abs(scroller.HorizontalOffset - 480) < 0.1, "Rapid wheel input accumulates every scroll step");
            await ScrollAt(blank, 120, horizontal: true);
            Observe("horizontal wheel");
            Check(Math.Abs(scroller.HorizontalOffset - 600) < 0.1, "Native horizontal wheel browses screenshot cards");

            scroller.ScrollToRightEnd();
            await Task.Delay(100);
            await ScrollAt(blank, -120);
            Observe("list end");
            Check(Math.Abs(scroller.HorizontalOffset - scroller.ScrollableWidth) < 0.1 && Received() == 0,
                "Wheel at the list end remains contained in the hot area");
            var beforeOutside = scroller.HorizontalOffset;
            await ScrollAt(outside, -120);
            Observe("outside hot area");
            Check(Received() == 1 && Math.Abs(scroller.HorizontalOffset - beforeOutside) < 0.1,
                "Wheel outside the hot area reaches the underlying process normally");
            dock.Hide();
            await ScrollAt(blank, -120);
            Observe("shelf hidden");
            Check(Received() == 2, "Hiding the shelf releases wheel interception");
            dock.Expand(TimeSpan.FromMinutes(1));
            await Task.Delay(500);
            scroller.ScrollToHorizontalOffset(0);
            await Task.Delay(100);
            await ScrollAt(blank, -120);
            Observe("shelf reopened");
            Check(Math.Abs(scroller.HorizontalOffset - 120) < 0.1 && Received() == 2,
                "Reopening the shelf restores hot-area wheel handling");

            var toolWheel = 0;
            var tool = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Width = 200, Height = 80, Background = Brushes.White };
            tool.PreviewMouseWheel += (_, e) => { toolWheel++; e.Handled = true; };
            try
            {
                tool.Show();
                var monitor = DesktopEnvironment.PrimaryMonitor;
                DesktopEnvironment.Place(tool, new PixelRect(monitor.Bounds.X + 20, monitor.Bounds.Y + (int)(250 * monitor.Scale), 200, 80));
                await ScrollAt(blank, -120);
                Observe("own tool window");
                Check(toolWheel == 1 && Math.Abs(scroller.HorizontalOffset - 120) < 0.1,
                    "An application tool window inside the hot area retains its own wheel input");
            }
            finally { tool.Close(); }

            dock.SetCards(cards);
            await Task.Delay(150);
            await ScrollAt(blank, -120);
            Observe("cards fit viewport");
            Check(scroller.ScrollableWidth == 0 && Received() == 2, "A non-overflowing shelf still prevents wheel input from reaching the background");
        }
        finally
        {
            if (!receiver.HasExited) { receiver.Kill(); await receiver.WaitForExitAsync(); }
            dock.SetCards(cards);
        }
    }

    private static void RunWheelTarget(string output)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        var monitor = DesktopEnvironment.PrimaryMonitor;
        using var form = new Forms.Form { Text = "ScreenshotDock wheel routing receiver", StartPosition = Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(monitor.Bounds.X + 20, monitor.Bounds.Y + (int)(220 * monitor.Scale)),
            Size = new System.Drawing.Size(380, 430), BackColor = System.Drawing.Color.Honeydew };
        var count = 0;
        var clicks = 0;
        form.MouseWheel += (_, _) => File.WriteAllText(Path.Combine(output, "count.txt"), (++count).ToString());
        form.MouseClick += (_, _) => File.WriteAllText(Path.Combine(output, "click.txt"), (++clicks).ToString());
        Forms.Application.Run(form);
    }

    private static async Task TestDockRetractionAsync(DockWindow dock)
    {
        var backdrop = _application.Windows.OfType<DockBackdropWindow>().Single();
        var retract = dock.CollapseAsync();
        if (SystemParameters.ClientAreaAnimation)
        {
            Check(dock.IsVisible && !retract.IsCompleted, "Shelf remains visible while retraction starts");
            await Task.Delay(120);
            SaveVisual(dock, "dock-retracting.png");
            SaveVisual(backdrop, "dock-shadow-retracting.png");
        }
        Check(await retract.WaitAsync(TimeSpan.FromSeconds(3)) && !dock.IsVisible && !backdrop.IsVisible, "Retraction hides the shelf and shadow after completion");

        if (!SystemParameters.ClientAreaAnimation) return;
        dock.Expand(TimeSpan.FromMinutes(1));
        await Task.Delay(500);
        var interrupted = dock.CollapseAsync();
        await Task.Delay(60);
        dock.Expand(TimeSpan.FromMinutes(1));
        var canceled = !await interrupted.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(500);
        Check(canceled && dock.IsVisible && backdrop.IsVisible && backdrop.ShadeOpacity == 1, "Reopening during retraction restores the shelf and shadow without a stale hide callback");

        var forced = dock.CollapseAsync();
        dock.Hide();
        Check(!await forced.WaitAsync(TimeSpan.FromSeconds(2)) && !dock.IsVisible && !backdrop.IsVisible, "Immediate hiding cancels pending retraction and gradient fading safely");
    }

    private static void RunDropTarget(string output)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        var monitor = DesktopEnvironment.PrimaryMonitor;
        using var form = new Forms.Form { Text = "ScreenshotDock OLE compatibility receiver", AllowDrop = true, TopMost = true,
            StartPosition = Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(monitor.WorkArea.X + 500, monitor.WorkArea.Y + 410),
            Size = new System.Drawing.Size(380, 280), BackColor = System.Drawing.Color.Honeydew };
        form.Controls.Add(new Forms.Label { Text = "PNG file drop receiver", AutoSize = true, Location = new System.Drawing.Point(35, 40) });
        form.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
        form.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(Forms.DataFormats.FileDrop) is string[] files && files.Length > 0)
                File.Copy(files[0], Path.Combine(output, "received.png"), overwrite: true);
        };
        Forms.Application.Run(form);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static BitmapSource SaveVisual(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(element);
        image.Freeze();
        File.WriteAllBytes(Path.Combine(_output, name), ScreenCaptureService.EncodePng(image));
        return image;
    }

    private static void SaveShelfComposition(BitmapSource shelf, BitmapSource shadow, string name)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, shelf.Width, shelf.Height);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 245, 248)), null, bounds);
            context.DrawImage(shadow, bounds);
            context.DrawImage(shelf, bounds);
        }
        var image = new RenderTargetBitmap(shelf.PixelWidth, shelf.PixelHeight, shelf.DpiX, shelf.DpiY, PixelFormats.Pbgra32);
        image.Render(drawing);
        File.WriteAllBytes(Path.Combine(_output, name), ScreenCaptureService.EncodePng(image));
    }

    private static BitmapSource CreateIllustration(int width, int height, string title, string color)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)new BrushConverter().ConvertFromString(color)!, null, new Rect(0, 0, width, height));
            var text = new FormattedText(title, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 26,
                color == "#233C31" ? Brushes.White : new SolidColorBrush(Color.FromRgb(40, 70, 50)), 1);
            drawing.DrawText(text, new Point(28, 35));
            for (var i = 0; i < 5; i++) drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null,
                new Rect(28, 105 + i * 37, width - 56 - i * 18, 20), 5, 5);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMenuItemRect(nint window, nint menu, uint index, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    private static void KeyDown(ushort key) => SendInput(1, [new() { Type = 1, Data = new() { Keyboard = new() { Key = key } } }], Marshal.SizeOf<Input>());
    private static void KeyUp(ushort key) => SendInput(1, [new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = 2 } } }], Marshal.SizeOf<Input>());
    private static void KeyPress(ushort key) { KeyDown(key); KeyUp(key); }
    private static uint Mouse(uint flags) => SendInput(1, [new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } }], Marshal.SizeOf<Input>());
    private static uint Wheel(short delta, bool horizontal) => SendInput(1, [new() { Type = 0, Data = new() { Mouse = new() { Data = unchecked((uint)(int)delta), Flags = horizontal ? 0x1000u : 0x0800u } } }], Marshal.SizeOf<Input>());
}
