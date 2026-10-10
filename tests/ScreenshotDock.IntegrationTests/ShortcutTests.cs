using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenshotDock.App;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Hotkeys;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static HotkeySettings TestShortcut(uint key) => new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        VirtualKey = key, KeyName = $"F{key - 0x6F}" };

    private static void PressShortcut(HotkeySettings shortcut)
    {
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Control)) KeyDown(0x11);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Alt)) KeyDown(0x12);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Shift)) KeyDown(0x10);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Windows)) KeyDown(0x5B);
        KeyPress((ushort)shortcut.VirtualKey);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Windows)) KeyUp(0x5B);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Shift)) KeyUp(0x10);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Alt)) KeyUp(0x12);
        if (shortcut.Modifiers.HasFlag(HotkeyModifiers.Control)) KeyUp(0x11);
    }

    private static async Task RunShortcutTestsAsync()
    {
        var captureKey = TestShortcut(0x73);
        var pinKey = TestShortcut(0x74);
        var replacement = TestShortcut(0x75);
        var conflict = TestShortcut(0x76);
        using (var pair = new ScreenshotHotkeyService())
        using (var probe = new GlobalHotkeyService())
        using (var occupant = new GlobalHotkeyService())
        {
            var captured = 0; var pinned = 0;
            pair.Pressed += action => { if (action == ScreenshotHotkeyAction.Capture) captured++; else pinned++; };
            Check(pair.TryRegister(captureKey, pinKey, out _), "Capture and pin combinations register as two global actions");
            PressShortcut(captureKey); PressShortcut(pinKey); await Task.Delay(120);
            Check(captured == 1 && pinned == 1, "Actual keyboard input dispatches the corresponding capture and pin actions");
            Check(pair.TryRegister(pinKey, captureKey, out _), "Swapping the two occupied keys reuses their registrations safely");
            PressShortcut(captureKey); await Task.Delay(80);
            Check(captured == 1 && pinned == 2, "A swapped key dispatches its new action rather than the previous action");
            Check(!pair.TryRegister(captureKey, captureKey, out var duplicate) && duplicate.Contains("不能相同"),
                "Duplicate keys are rejected before changing either active registration");
            Check(occupant.TryRegister(conflict, out _), "A dedicated native listener occupies the replacement pin key");
            Check(!pair.TryRegister(replacement, conflict, out var error) && error.Contains("贴图") && error.Contains("占用"),
                "A conflict on the second replacement key identifies the pin shortcut");
            Check(!probe.TryRegister(captureKey, out _) && probe.TryRegister(replacement, out _),
                "A failed pair change retains both old keys and releases the temporary first replacement");
            probe.SetPaused(true, out _);
            PressShortcut(pinKey); await Task.Delay(80);
            Check(captured == 2, "The original capture action remains usable after a failed pair replacement");
            Check(pair.SetPaused(true, out _) && probe.TryRegister(captureKey, out _) && probe.SetPaused(false, out _),
                "Pausing releases both global shortcuts");
            Check(!pair.SetPaused(false, out _) && pair.IsPaused, "A resume conflict keeps the whole pair paused");
            Check(occupant.TryRegister(pinKey, out _), "Failed resume does not leave only the capture shortcut registered");
            probe.SetPaused(true, out _); occupant.SetPaused(true, out _);
            Check(pair.SetPaused(false, out _) && !pair.IsPaused, "Both shortcuts resume after conflicts are released");
            var persistenceFailed = false;
            try { pair.TryRegister(replacement, conflict, out _, () => throw new IOException("Isolated settings write failure")); }
            catch (IOException) { persistenceFailed = true; }
            Check(persistenceFailed && !probe.TryRegister(captureKey, out _) && probe.TryRegister(replacement, out _),
                "A settings write failure retains old bindings and releases both proposed replacements");
            probe.SetPaused(true, out _);
        }

        using (var defaults = new ScreenshotHotkeyService())
        {
            var captureCount = 0; var pinCount = 0;
            defaults.Pressed += action => { if (action == ScreenshotHotkeyAction.Capture) captureCount++; else pinCount++; };
            Check(defaults.TryRegister(new(), new() { VirtualKey = 0x72, KeyName = "F3" }, out _),
                "Default F1 and F3 register globally without modifier keys");
            KeyPress(0x70); KeyPress(0x72); await Task.Delay(100);
            Check(captureCount == 1 && pinCount == 1, "Actual F1 and F3 keys dispatch capture and pin separately");
        }

        await VerifyShortcutSettingsAsync();
        await VerifyShortcutPinPipelineAsync(captureKey, pinKey);
        Check(_application.Windows.Count == 0, "Shortcut verification releases every capture, settings and pin window");
        Console.WriteLine($"{Checks.Count} screenshot and pin shortcut checks passed.");
    }

    private static async Task VerifyShortcutSettingsAsync()
    {
        var foreground = GetForegroundWindow();
        AppSettings? saved = null;
        var calls = 0;
        var settings = new SettingsWindow(new(), value => { saved = value; calls++; return Task.FromResult<string?>(null); }, new FailingStartupRegistration());
        try
        {
            settings.Show(); settings.Activate();
            var capture = (TextBox)settings.FindName("HotkeyBox");
            var pin = (TextBox)settings.FindName("PinHotkeyBox");
            Check(capture.Text == "F1" && pin.Text == "F3", "The two settings recorders show the new F1 and F3 defaults");
            Check(!Descendants(settings).OfType<ScrollViewer>().Any(v => v.ScrollableHeight > 0), "Both shortcut fields fit in settings without a page scrollbar");
            capture.Focus(); KeyPress(0x71); await Task.Delay(80);
            pin.Focus(); KeyPress(0x71); await Task.Delay(80);
            ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calls == 0 && ((TextBlock)settings.FindName("ErrorLabel")).Text.Contains("不能相同"),
                "The settings form rejects duplicate bindings without saving");
            pin.Focus();
            KeyDown(0x11); KeyDown(0x10); KeyPress(0x74); KeyUp(0x10); KeyUp(0x11); await Task.Delay(100);
            Check(capture.Text == "F2" && pin.Text == "Ctrl + Shift + F5", "Independent recorders accept a single function key and a modifier combination");
            SaveVisual(settings, "settings-shortcuts-custom.png");
            ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(40);
            Check(saved?.Hotkey.Display == "F2" && saved.PinHotkey.Display == "Ctrl + Shift + F5", "Saving settings includes both customized shortcut bindings");
        }
        finally { settings.Close(); SetForegroundWindow(foreground); }
        settings = new(saved!, _ => Task.FromResult<string?>(null), new FailingStartupRegistration());
        try
        {
            settings.Show();
            ((Button)settings.FindName("ResetCaptureHotkeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBox)settings.FindName("HotkeyBox")).Text == "F1" && ((TextBox)settings.FindName("PinHotkeyBox")).Text == "Ctrl + Shift + F5",
                "Resetting capture restores F1 without changing the customized pin shortcut");
            ((Button)settings.FindName("ResetPinHotkeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBox)settings.FindName("PinHotkeyBox")).Text == "F3", "Resetting pin independently restores F3");
            await Task.Delay(30); SaveVisual(settings, "settings-shortcuts-default.png");
        }
        finally { settings.Close(); }
    }

    private static async Task VerifyShortcutPinPipelineAsync(HotkeySettings captureKey, HotkeySettings pinKey)
    {
        var foreground = GetForegroundWindow();
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var root = Path.Combine(_output, "app-data");
        var repository = new ScreenshotRepository(root);
        repository.Load(new(), DateTimeOffset.UtcNow); repository.Clear();
        new SettingsStore(root).Save(new() { Hotkey = captureKey, PinHotkey = pinKey, AutoCopy = false, AutoExpand = false, SuppressInFullscreen = false });
        using var controller = new AppController(root, startup: new FailingStartupRegistration());
        await controller.StartAsync(false);
        var fixture = new Window { Title = "ScreenshotDock shortcut fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White, ShowActivated = false, Topmost = true, Width = 560, Height = 320 };
        fixture.Show(); DesktopEnvironment.Place(fixture, new(monitor.Bounds.X + 300, monitor.Bounds.Y + 320, 560, 320));
        try
        {
            await Task.Delay(60);
            PressShortcut(captureKey);
            for (var i = 0; i < 100 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(10);
            var overlays = _application.Windows.OfType<CaptureWindow>().ToArray();
            Check(overlays.Length == DesktopEnvironment.Monitors.Count, "The customized global capture key opens the real multi-monitor screenshot flow");
            var selection = new PixelRect(monitor.Bounds.X + 340, monitor.Bounds.Y + 350, 460, 230);
            foreach (var overlay in overlays) { DesktopEnvironment.DoNotActivate(overlay); DesktopEnvironment.MakeClickThrough(overlay); overlay.IsHitTestVisible = false; }
            var window = overlays.First(w => w.PointToScreen(new Point()).X == monitor.Bounds.X);
            Check(((Button)window.FindName("PinCaptureButton")).ToolTip?.ToString()?.Contains(pinKey.Display) == true,
                "The capture toolbar displays the customized pin shortcut");
            PressShortcut(pinKey); await Task.Delay(80);
            Check(_application.Windows.OfType<CaptureWindow>().Any() && !_application.Windows.OfType<PinnedImageWindow>().Any(),
                "The pin key before choosing a region keeps capture active instead of pinning an unrelated image");
            window.Editor.Selection.SetSelection(selection);
            ClickEditor(window, "TextToolButton"); window.Editor.BeginStroke(new(selection.X + 30, selection.Y + 80), 1);
            ((TextBox)window.FindName("AnnotationTextBox")).Text = "快捷键贴图";
            PressShortcut(pinKey);
            for (var i = 0; i < 100 && !_application.Windows.OfType<PinnedImageWindow>().Any(); i++) await Task.Delay(20);
            var first = _application.Windows.OfType<PinnedImageWindow>().Single();
            Check(!_application.Windows.OfType<CaptureWindow>().Any() && first.Image.PixelWidth == 460 && first.Image.PixelHeight == 230,
                "The global pin key commits the current selection and exits capture");
            repository.Load(new(), DateTimeOffset.UtcNow);
            var retained = ScreenCaptureService.LoadImage(repository.GetImagePath(repository.Items[0].Id));
            var textPixels = new byte[first.Image.PixelWidth * first.Image.PixelHeight * 4];
            new System.Windows.Media.Imaging.FormatConvertedBitmap(first.Image, PixelFormats.Bgra32, null, 0)
                .CopyPixels(textPixels, first.Image.PixelWidth * 4, 0);
            Check(ScreenCaptureService.EncodePng(first.Image).SequenceEqual(ScreenCaptureService.EncodePng(retained))
                && Enumerable.Range(0, textPixels.Length / 4).Count(i => textPixels[i * 4 + 2] > 230
                    && textPixels[i * 4 + 1] < 120 && textPixels[i * 4] < 120) > 30,
                "Shortcut pinning commits pending text and uses the same flattened image as the cache");
            PressShortcut(pinKey); await Task.Delay(120);
            var pins = _application.Windows.OfType<PinnedImageWindow>().ToArray();
            Check(pins.Length == 2 && pins.All(p => ScreenCaptureService.EncodePng(p.Image).SequenceEqual(ScreenCaptureService.EncodePng(retained))),
                "The global pin key after capture creates another independent pin of the most recent screenshot");
            var last = pins.Single(p => p != first);
            Check(Math.Abs(last.Left - first.Left) < 1 && Math.Abs(last.Top - first.Top) < 1,
                "The most recent screenshot retains its original screen position for later pinning");
            last.Close(); first.Close();
            var dock = _application.Windows.OfType<DockWindow>().Single();
            ((Button)dock.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var probe = new GlobalHotkeyService();
            Check(probe.TryRegister(captureKey, out _) && probe.TryRegister(pinKey, out _),
                "Opening real settings temporarily releases both global bindings for key recording");
            probe.SetPaused(true, out _);
            _application.Windows.OfType<SettingsWindow>().Single().Close();
            Check(!probe.TryRegister(captureKey, out _) && !probe.TryRegister(pinKey, out _),
                "Closing real settings restores both global shortcut registrations");
        }
        finally { fixture.Close(); controller.Dispose(); SetForegroundWindow(foreground); }
    }
}
