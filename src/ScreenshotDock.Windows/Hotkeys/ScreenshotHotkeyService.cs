using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.Windows.Hotkeys;

public enum ScreenshotHotkeyAction { Capture, Pin }

public sealed class ScreenshotHotkeyService : IDisposable
{
    private sealed record Binding(int Id, ScreenshotHotkeyAction Action, HotkeySettings Settings);
    private readonly HwndSource _source;
    private List<Binding> _bindings = [];
    private (HotkeySettings Capture, HotkeySettings Pin)? _current;
    private int _nextId = 100;
    public bool IsPaused { get; private set; }
    public nint Handle => _source.Handle;
    public event Action<ScreenshotHotkeyAction>? Pressed;

    public ScreenshotHotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("ScreenshotDock.Shortcuts")
            { ParentWindow = new nint(-3), Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    public bool TryRegister(HotkeySettings capture, HotkeySettings pin, out string error, Action? beforeCommit = null)
    {
        error = "";
        if (!capture.IsValid || !pin.IsValid) { error = "截图和贴图快捷键支持 F1–F11、字母、数字、PrintScreen 单键或组合键。"; return false; }
        if (capture.SameBinding(pin)) { error = "截图快捷键与贴图快捷键不能相同。"; return false; }
        var candidates = new List<Binding>();
        var added = new List<int>();
        foreach (var (action, settings) in new[] { (ScreenshotHotkeyAction.Capture, capture), (ScreenshotHotkeyAction.Pin, pin) })
        {
            var previous = _bindings.FirstOrDefault(b => b.Settings.SameBinding(settings));
            var id = previous?.Id ?? NextId();
            if (previous is null)
            {
                if (!NativeMethods.RegisterHotKey(Handle, id, (uint)settings.Modifiers | 0x4000, settings.VirtualKey))
                {
                    var code = Marshal.GetLastWin32Error();
                    foreach (var registered in added) NativeMethods.UnregisterHotKey(Handle, registered);
                    error = $"{(action == ScreenshotHotkeyAction.Capture ? "截图" : "贴图")}快捷键 {settings.Display} 已被占用或被系统保留，请更换。（错误 {code}）";
                    return false;
                }
                added.Add(id);
            }
            candidates.Add(new(id, action, settings));
        }
        try { beforeCommit?.Invoke(); }
        catch { foreach (var registered in added) NativeMethods.UnregisterHotKey(Handle, registered); throw; }
        // Reuse existing IDs when swapping the two keys. Commit only after both replacements and persistence succeed.
        foreach (var binding in _bindings.Where(b => candidates.All(c => c.Id != b.Id))) NativeMethods.UnregisterHotKey(Handle, binding.Id);
        _bindings = candidates;
        _current = (capture, pin);
        if (IsPaused) ReleaseBindings(); // Probe conflicts during settings editing, then leave key input available to the recorder.
        return true;
    }

    private int NextId()
    {
        do { _nextId = _nextId >= 0xBFFF ? 101 : _nextId + 1; } while (_bindings.Any(b => b.Id == _nextId));
        return _nextId;
    }

    public bool SetPaused(bool paused, out string error)
    {
        error = "";
        if (paused) { ReleaseBindings(); IsPaused = true; return true; }
        if (!IsPaused && _bindings.Count == 2) return true;
        if (_current is not { } current) { error = "请在设置中保存可用的截图与贴图快捷键。"; return false; }
        IsPaused = false;
        if (TryRegister(current.Capture, current.Pin, out error)) return true;
        IsPaused = true;
        return false;
    }

    private void ReleaseBindings()
    {
        foreach (var binding in _bindings) NativeMethods.UnregisterHotKey(Handle, binding.Id);
        _bindings.Clear();
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && !IsPaused && _bindings.FirstOrDefault(b => b.Id == wParam.ToInt32()) is { } binding)
        { handled = true; Pressed?.Invoke(binding.Action); }
        return 0;
    }
    public void Dispose() { ReleaseBindings(); _source.RemoveHook(WndProc); _source.Dispose(); }
}
