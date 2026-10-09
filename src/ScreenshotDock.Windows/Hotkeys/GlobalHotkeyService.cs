using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.Windows.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private int _activeId;
    private int _nextId = 100;
    private HotkeySettings? _current;
    public event EventHandler? Pressed;
    public bool IsPaused { get; private set; }
    public nint Handle => _source.Handle;

    public GlobalHotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("ScreenshotDock.Hotkey") { ParentWindow = new nint(-3), Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    public bool TryRegister(HotkeySettings settings, out string error)
    {
        error = "";
        if (!settings.IsValid) { error = "请选择 F1–F11、字母、数字、PrintScreen 单键或组合键；F12 为系统保留键。"; return false; }
        if (_activeId != 0 && _current?.Modifiers == settings.Modifiers && _current.VirtualKey == settings.VirtualKey) return true;
        var candidate = ++_nextId;
        if (!NativeMethods.RegisterHotKey(_source.Handle, candidate, (uint)settings.Modifiers | 0x4000, settings.VirtualKey))
        {
            error = $"快捷键 {settings.Display} 已被占用或被系统保留，请换一个组合。（错误 {Marshal.GetLastWin32Error()}）";
            return false;
        }
        // Register the replacement first: a conflict must not remove the working shortcut.
        if (_activeId != 0) NativeMethods.UnregisterHotKey(_source.Handle, _activeId);
        _activeId = candidate;
        _current = settings;
        if (IsPaused) { NativeMethods.UnregisterHotKey(_source.Handle, _activeId); _activeId = 0; }
        return true;
    }

    public bool SetPaused(bool paused, out string error)
    {
        error = "";
        if (paused)
        {
            if (_activeId != 0) NativeMethods.UnregisterHotKey(_source.Handle, _activeId);
            _activeId = 0;
            IsPaused = true;
            return true;
        }
        IsPaused = false;
        if (_current is null)
        {
            IsPaused = true;
            error = "尚无可用的截图快捷键，请在设置中选择并保存快捷键。";
            return false;
        }
        if (TryRegister(_current, out error)) return true;
        IsPaused = true;
        return false;
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == _activeId && !IsPaused)
        { handled = true; Pressed?.Invoke(this, EventArgs.Empty); }
        return 0;
    }

    public void Dispose()
    {
        if (_activeId != 0) NativeMethods.UnregisterHotKey(_source.Handle, _activeId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
