using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenshotDock.Windows.Tray;

public sealed class TrayService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private bool _paused;
    private bool _disposed;
    public nint Handle => _source.Handle;
    public event Action? CaptureRequested;
    public event Action? DockRequested;
    public event Action? SettingsRequested;
    public event Action? PauseRequested;
    public event Action? ExitRequested;

    public TrayService(Stream iconStream)
    {
        using var buffer = new MemoryStream();
        iconStream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 22) throw new InvalidDataException("应用图标无效。");
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14, 4));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        if (size <= 0 || offset < 22 || offset > bytes.Length - size) throw new InvalidDataException("应用图标数据无效。");
        _icon = CreateIconFromResourceEx(bytes.AsSpan(offset, size).ToArray(), (uint)size, true, 0x00030000, 32, 32, 0);
        if (_icon == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _source = new HwndSource(new HwndSourceParameters("ScreenshotDock.Tray")
        { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x00000080 });
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        AddIcon();
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon, Tip = "ScreenshotDock · 截图即收纳"
    };

    private void AddIcon()
    {
        var data = Data();
        if (!ShellNotifyIcon(0, ref data)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建系统托盘图标。");
    }

    public void SetPaused(bool paused) => _paused = paused;
    public void Notify(string message)
    {
        var data = Data();
        data.Flags = 0x10;
        data.Info = message.Length <= 255 ? message : message[..255];
        data.InfoTitle = "ScreenshotDock";
        data.InfoFlags = 1;
        ShellNotifyIcon(1, ref data);
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) { AddIcon(); handled = true; }
        if ((uint)message != CallbackMessage) return 0;
        handled = true;
        if (lParam.ToInt32() == 0x0203) DockRequested?.Invoke();
        if (lParam.ToInt32() is 0x0205 or 0x007B) ShowMenu();
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "打开截图栏");
            AppendMenu(menu, 0, 2, "立即截图");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 3, "设置");
            AppendMenu(menu, _paused ? 8u : 0u, 4, _paused ? "恢复快捷键" : "暂停快捷键");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 5, "退出 ScreenshotDock");
            GetCursorPos(out var cursor);
            SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenuEx(menu, 0x100 | 2, cursor.X, cursor.Y, _source.Handle, 0);
            PostMessage(_source.Handle, 0, 0, 0);
            switch (command)
            {
                case 1: DockRequested?.Invoke(); break;
                case 2: CaptureRequested?.Invoke(); break;
                case 3: SettingsRequested?.Invoke(); break;
                case 4: PauseRequested?.Invoke(); break;
                case 5: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint options);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
