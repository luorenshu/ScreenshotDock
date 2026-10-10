using System.Windows.Input;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Startup;

namespace ScreenshotDock.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _initial;
    private HotkeySettings _hotkey;
    private HotkeySettings _pinHotkey;
    private readonly Func<AppSettings, Task<string?>> _save;
    private readonly IStartupRegistration _startup;
    private bool _startupReady;
    private bool _startupEnabled;
    public SettingsWindow(AppSettings settings, Func<AppSettings, Task<string?>> save, IStartupRegistration? startup = null)
    {
        InitializeComponent();
        _initial = settings;
        _hotkey = settings.Hotkey;
        _pinHotkey = settings.PinHotkey;
        _save = save;
        _startup = startup ?? new StartupRegistration();
        HotkeyBox.Text = _hotkey.Display;
        PinHotkeyBox.Text = _pinHotkey.Display;
        MaxItemsBox.Text = settings.Storage.MaxItems.ToString();
        RetentionBox.Text = settings.Storage.RetentionDays.ToString();
        CleanupBox.IsChecked = settings.Storage.AutoCleanup;
        CopyBox.IsChecked = settings.AutoCopy;
        ExpandBox.IsChecked = settings.AutoExpand;
        FullscreenBox.IsChecked = settings.SuppressInFullscreen;
        try { _startupEnabled = _startup.IsEnabled; StartupBox.IsChecked = _startupEnabled; }
        catch (Exception ex) { StartupBox.IsEnabled = false; ShowError($"无法读取开机启动设置：{ex.Message}"); }
        _startupReady = true;
    }

    public void ShowError(string message) => ErrorLabel.Text = message;

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!_startupReady) return;
        try
        {
            _startup.SetEnabled(StartupBox.IsChecked == true);
            _startupEnabled = StartupBox.IsChecked == true;
            ErrorLabel.Text = "";
        }
        catch (Exception ex)
        {
            _startupReady = false;
            StartupBox.IsChecked = _startupEnabled;
            _startupReady = true;
            ShowError($"开机启动未能修改：{ex.Message}");
        }
    }

    private void HotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        var modifiers = HotkeyModifiers.None;
        if (mods.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (mods.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (mods.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        var keyName = key >= Key.D0 && key <= Key.D9 ? key.ToString()[1..] : key == Key.Snapshot ? "PrintScreen" : key.ToString();
        var candidate = new HotkeySettings { Modifiers = modifiers, VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key), KeyName = keyName };
        if (!candidate.IsValid) { ErrorLabel.Text = "支持单键 F1–F11、字母、数字、PrintScreen，也支持组合键；F12 为系统保留键。"; return; }
        if (ReferenceEquals(sender, PinHotkeyBox)) { _pinHotkey = candidate; PinHotkeyBox.Text = candidate.Display; }
        else { _hotkey = candidate; HotkeyBox.Text = candidate.Display; }
        ErrorLabel.Text = "";
    }

    private void ResetHotkeyClick(object sender, RoutedEventArgs e) { _hotkey = new(); HotkeyBox.Text = _hotkey.Display; ErrorLabel.Text = ""; }
    private void ResetPinHotkeyClick(object sender, RoutedEventArgs e)
    { _pinHotkey = new() { VirtualKey = 0x72, KeyName = "F3" }; PinHotkeyBox.Text = _pinHotkey.Display; ErrorLabel.Text = ""; }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_hotkey.SameBinding(_pinHotkey)) { ErrorLabel.Text = "截图快捷键与贴图快捷键不能相同。"; return; }
        if (!int.TryParse(MaxItemsBox.Text, out var count) || count is < 1 or > 1000)
        { ErrorLabel.Text = "暂存数量请输入 1–1000 之间的整数。"; return; }
        if (!int.TryParse(RetentionBox.Text, out var days) || days is < 1 or > 365)
        { ErrorLabel.Text = "保留天数请输入 1–365 之间的整数。"; return; }
        SaveButton.IsEnabled = false;
        var updated = _initial with { Hotkey = _hotkey, PinHotkey = _pinHotkey, Storage = new() { MaxItems = count, RetentionDays = days, AutoCleanup = CleanupBox.IsChecked == true },
            AutoCopy = CopyBox.IsChecked == true, AutoExpand = ExpandBox.IsChecked == true, SuppressInFullscreen = FullscreenBox.IsChecked == true };
        try
        {
            var error = await _save(updated);
            if (error is null) Close();
            else ErrorLabel.Text = error;
        }
        catch (Exception ex) { ErrorLabel.Text = $"设置未能保存：{ex.Message}"; }
        finally { SaveButton.IsEnabled = true; }
    }
}
