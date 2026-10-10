using ScreenshotDock.Core.Models;

namespace ScreenshotDock.Core.Services;

public sealed class SettingsStore(string root)
{
    public string FilePath { get; } = Path.Combine(root, "settings.json");
    public AppSettings Load()
    {
        var settings = (AtomicJson.Read<AppSettings>(FilePath)
            ?? AtomicJson.Read<AppSettings>(FilePath + ".bak") ?? new()).Normalize();
        // Migrate the previous shipped default once; explicitly saved custom combinations remain intact.
        if (settings.ShortcutDefaultsVersion == 0 && settings.Hotkey.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Shift)
            && settings.Hotkey.VirtualKey == 0x41) settings = settings with { Hotkey = new() };
        return settings with { ShortcutDefaultsVersion = 1 };
    }
    public void Save(AppSettings settings) => AtomicJson.Write(FilePath, settings.Normalize() with { ShortcutDefaultsVersion = 1 });
}
