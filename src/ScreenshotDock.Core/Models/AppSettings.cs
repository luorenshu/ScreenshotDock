namespace ScreenshotDock.Core.Models;

[Flags]
public enum HotkeyModifiers : uint { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

public sealed record HotkeySettings
{
    public HotkeyModifiers Modifiers { get; init; } = HotkeyModifiers.None;
    public uint VirtualKey { get; init; } = 0x70;
    public string KeyName { get; init; } = "F1";
    public bool SameBinding(HotkeySettings other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
    public string Display => string.Join(" + ", new[]
    {
        Modifiers.HasFlag(HotkeyModifiers.Control) ? "Ctrl" : null,
        Modifiers.HasFlag(HotkeyModifiers.Alt) ? "Alt" : null,
        Modifiers.HasFlag(HotkeyModifiers.Shift) ? "Shift" : null,
        Modifiers.HasFlag(HotkeyModifiers.Windows) ? "Win" : null,
        KeyName
    }.Where(s => s is not null));

    public bool IsValid => ((uint)Modifiers & ~15u) == 0
        && ((VirtualKey >= 0x30 && VirtualKey <= 0x39) || (VirtualKey >= 0x41 && VirtualKey <= 0x5A)
        || (VirtualKey >= 0x70 && VirtualKey <= 0x7A)
        || VirtualKey == 0x2C) && !string.IsNullOrWhiteSpace(KeyName);
}

public sealed record StorageSettings
{
    public int MaxItems { get; init; } = 100;
    public int RetentionDays { get; init; } = 7;
    public bool AutoCleanup { get; init; } = true;
}

public sealed record AppSettings
{
    public HotkeySettings Hotkey { get; init; } = new();
    public HotkeySettings PinHotkey { get; init; } = new() { VirtualKey = 0x72, KeyName = "F3" };
    public int ShortcutDefaultsVersion { get; init; }
    public StorageSettings Storage { get; init; } = new();
    public bool AutoCopy { get; init; } = true;
    public bool AutoExpand { get; init; } = true;
    public bool SuppressInFullscreen { get; init; } = true;
    public bool DockLocked { get; init; }

    public AppSettings Normalize() => this with
    {
        Hotkey = Hotkey is { IsValid: true } ? Hotkey : new(),
        PinHotkey = PinHotkey is { IsValid: true } ? PinHotkey : new() { VirtualKey = 0x72, KeyName = "F3" },
        Storage = (Storage ?? new()) with
        {
            MaxItems = Math.Clamp(Storage?.MaxItems ?? 100, 1, 1000),
            RetentionDays = Math.Clamp(Storage?.RetentionDays ?? 7, 1, 365)
        }
    };
}
