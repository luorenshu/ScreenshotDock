using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScreenshotDock.App;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;
using ScreenshotDock.Windows.Startup;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static async Task RunSettingsTestsAsync()
    {
        // Keep registration tests away from Windows' real login startup key.
        var registryPath = @"Software\ScreenshotDock\Verification\" + Guid.NewGuid().ToString("N");
        var executableDirectory = Path.Combine(_output, "startup app");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "Screenshot Dock.exe");
        File.Copy(Environment.ProcessPath!, executable, overwrite: true);
        var startup = new StartupRegistration(executable, registryPath);
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) key.SetValue("OtherProgram", "preserved");
            Check(!startup.IsEnabled, "Startup is disabled when no registration exists");
            startup.SetEnabled(true);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath)!)
                Check(key.GetValue("ScreenshotDock") as string == $"\"{executable}\" --startup"
                    && key.GetValueKind("ScreenshotDock") == RegistryValueKind.String,
                    "Startup stores a quoted executable path with spaces and the background-launch argument");
            Check(new StartupRegistration(executable, registryPath).IsEnabled, "Startup registration persists across service instances");
            startup.SetEnabled(false);
            startup.SetEnabled(false);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath)!)
                Check(!startup.IsEnabled && key.GetValue("OtherProgram") as string == "preserved",
                    "Disabling startup is repeatable and preserves other registry entries");

            var saveCalls = 0;
            var settings = new SettingsWindow(new(), _ => { saveCalls++; return Task.FromResult<string?>(null); }, startup);
            try
            {
                settings.Show();
                var toggle = (CheckBox)settings.FindName("StartupBox");
                toggle.IsChecked = true;
                Check(startup.IsEnabled && saveCalls == 0, "Checking startup takes effect before clicking Save Settings");
                await Task.Delay(40);
                SaveVisual(settings, "settings-startup-on.png");
                Descendants(settings).OfType<Button>().Single(b => b.Content as string == "取消").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(startup.IsEnabled && saveCalls == 0, "Closing settings does not revert the immediate startup switch");
            }
            finally { settings.Close(); }

            AppSettings? saved = null;
            settings = new(new(), value => { saved = value; return Task.FromResult<string?>(null); }, startup);
            try
            {
                settings.Show();
                var toggle = (CheckBox)settings.FindName("StartupBox");
                Check(toggle.IsChecked == true, "Reopened settings reflects the saved startup registration");
                toggle.IsChecked = false;
                Check(!startup.IsEnabled && saved is null, "Unchecking startup immediately removes the registration");
                ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(20);
                Check(saved is { AutoCopy: true, AutoExpand: true } && saved.Storage.MaxItems == 100 && !startup.IsEnabled,
                    "Saving the other preferences keeps the startup switch and existing defaults intact");
            }
            finally { settings.Close(); }

            var failingStartup = new FailingStartupRegistration { FailWrites = true };
            settings = new(new(), _ => Task.FromResult<string?>(null), failingStartup);
            try
            {
                settings.Show();
                var toggle = (CheckBox)settings.FindName("StartupBox");
                toggle.IsChecked = true;
                var error = (TextBlock)settings.FindName("ErrorLabel");
                Check(toggle.IsChecked == false && !failingStartup.IsEnabled && error.Text.Contains("开机启动未能修改"),
                    "A failed enable restores the unchecked state and explains the failure");
                await Task.Delay(40);
                SaveVisual(settings, "settings-startup-error.png");
                failingStartup.FailWrites = false;
                toggle.IsChecked = true;
                Check(failingStartup.IsEnabled && error.Text == "", "Startup can be retried successfully without reopening settings");
                failingStartup.FailWrites = true;
                toggle.IsChecked = false;
                Check(toggle.IsChecked == true && failingStartup.IsEnabled, "A failed disable preserves the enabled state");
            }
            finally { settings.Close(); }

            await TestClearConfirmationAsync(startup);
            await RenderThemePreviewAsync();
            Check(_application.Windows.Count == 0, "Settings and confirmation checks close every application window");
            Console.WriteLine($"{Checks.Count} settings and clear-confirmation checks passed.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }
    }

    private static async Task TestClearConfirmationAsync(IStartupRegistration startup)
    {
        var root = Path.Combine(_output, "clear-app-data");
        var cache = new ScreenshotRepository(root);
        cache.Load(new(), DateTimeOffset.UtcNow);
        cache.Clear();
        foreach (var width in new[] { 240, 300 })
        {
            var image = EditorFixture(width, 160);
            cache.Add(ScreenCaptureService.EncodePng(image), ScreenCaptureService.Thumbnail(image), new(), DateTimeOffset.UtcNow);
        }
        new SettingsStore(root).Save(new() { AutoCopy = false, SuppressInFullscreen = false,
            Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x78, KeyName = "F9" } });
        var monitor = DesktopEnvironment.PrimaryMonitor;
        using var controller = new AppController(root, edgeFactory: () => new EdgeWindow(
            () => new(monitor.Bounds.X + 40, monitor.Bounds.Y + 40), () => false), startup: startup);
        await controller.StartAsync(showInitialDock: false);
        var dock = _application.Windows.OfType<DockWindow>().Single();
        Check(!dock.IsVisible && _application.Windows.OfType<EdgeWindow>().Single().IsVisible,
            "Background startup keeps the shelf hidden and enables its top-edge trigger");
        dock.Expand(TimeSpan.FromSeconds(30));
        dock.SetLocked(true);
        ((Button)dock.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var appSettings = _application.Windows.OfType<SettingsWindow>().Single();
        ((CheckBox)appSettings.FindName("StartupBox")).IsChecked = true;
        Check(startup.IsEnabled, "The real application's settings uses the injected Windows startup service");
        appSettings.Close();

        async Task RespondToClearAsync(string response)
        {
            var responseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _application.Dispatcher.BeginInvoke(new Action(async () =>
            {
                ClearConfirmationWindow? dialog = null;
                try
                {
                    await Task.Delay(50);
                    dialog = _application.Windows.OfType<ClearConfirmationWindow>().Single();
                    Check(dock.IsBusy && dock.IsConfirmationOpen && cache.Items.All(i => File.Exists(cache.GetImagePath(i.Id))),
                        "Clear confirmation pauses shelf interactions before removing files: " + response);
                    await controller.CaptureAsync();
                    Check(!_application.Windows.OfType<CaptureWindow>().Any(), "Capture cannot bypass an open clear confirmation: " + response);
                    if (response == "cancel") SaveVisual(dialog, "clear-confirmation.png");
                    if (response == "close") dialog.Close();
                    else ((Button)dialog.FindName(response == "confirm" ? "ConfirmButton" : "CancelButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    responseCompletion.SetResult();
                }
                catch (Exception exception) { dialog?.Close(); responseCompletion.SetException(exception); }
            }));
            ((Button)dock.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await responseCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await RespondToClearAsync("cancel");
        cache.Load(new(), DateTimeOffset.UtcNow);
        Check(cache.Items.Count == 2 && !dock.IsConfirmationOpen, "Cancel keeps every temporary screenshot and releases the confirmation state");
        await RespondToClearAsync("close");
        cache.Load(new(), DateTimeOffset.UtcNow);
        Check(cache.Items.Count == 2, "Closing the confirmation window keeps every temporary screenshot");
        await RespondToClearAsync("confirm");
        for (var i = 0; i < 100 && ((Button)dock.FindName("ClearButton")).IsEnabled; i++) await Task.Delay(10);
        cache.Load(new(), DateTimeOffset.UtcNow);
        Check(cache.Items.Count == 0 && !((Button)dock.FindName("ClearButton")).IsEnabled && !dock.IsConfirmationOpen,
            "Only confirming clear removes all cached screenshots and refreshes the shelf");
        controller.Dispose();
    }

    private sealed class FailingStartupRegistration : IStartupRegistration
    {
        public bool IsEnabled { get; private set; }
        public bool FailWrites { get; set; }
        public void SetEnabled(bool enabled)
        {
            if (FailWrites) throw new UnauthorizedAccessException("测试写入被拒绝");
            IsEnabled = enabled;
        }
    }
}
