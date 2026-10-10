using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using Xunit;

namespace ScreenshotDock.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData(0x70u, "F1")]
    [InlineData(0x71u, "F2")]
    [InlineData(0x7Au, "F11")]
    [InlineData(0x41u, "A")]
    [InlineData(0x32u, "2")]
    [InlineData(0x2Cu, "PrintScreen")]
    public void SingleKeyRemainsValidWhenSettingsAreNormalizedAndReloaded(uint key, string name)
    {
        var hotkey = new HotkeySettings { Modifiers = HotkeyModifiers.None, VirtualKey = key, KeyName = name };
        Assert.True(hotkey.IsValid);
        Assert.Equal(name, hotkey.Display);
        var root = Path.Combine(Path.GetTempPath(), "ScreenshotDock.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            store.Save(new() { Hotkey = hotkey });
            Assert.Equal(hotkey, store.Load().Hotkey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0x7Bu)] // F12 is reserved by Windows debuggers.
    [InlineData(0x10u)] // Modifier alone.
    [InlineData(0x3Fu)] // Not a supported virtual key.
    public void UnsupportedSingleKeysAreRejected(uint key) => Assert.False(new HotkeySettings
        { Modifiers = HotkeyModifiers.None, VirtualKey = key, KeyName = "Unsupported" }.IsValid);

    [Fact]
    public void ModifierCombinationsKeepTheirDisplayFormat() => Assert.Equal("Ctrl + Shift + F2", new HotkeySettings
        { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey = 0x71, KeyName = "F2" }.Display);

    [Fact]
    public void FreshAndInvalidSettingsUseSeparateF1AndF3Defaults()
    {
        var settings = new AppSettings { Hotkey = null!, PinHotkey = null! }.Normalize();
        Assert.Equal("F1", settings.Hotkey.Display);
        Assert.Equal("F3", settings.PinHotkey.Display);
        Assert.False(settings.Hotkey.SameBinding(settings.PinHotkey));
    }

    [Fact]
    public void LegacyDefaultMigratesButExplicitlySavedCustomKeysDoNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ScreenshotDock.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            var legacy = new AppSettings { Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
                VirtualKey = 0x41, KeyName = "A" } };
            AtomicJson.Write(store.FilePath, legacy);
            Assert.Equal("F1", store.Load().Hotkey.Display);
            Assert.Equal("F3", store.Load().PinHotkey.Display);
            store.Save(legacy);
            Assert.Equal("Ctrl + Shift + A", store.Load().Hotkey.Display);
            var custom = legacy with { Hotkey = legacy.Hotkey with { VirtualKey = 0x71, KeyName = "F2" } };
            AtomicJson.Write(store.FilePath, custom);
            Assert.Equal("Ctrl + Shift + F2", store.Load().Hotkey.Display);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Shift)]
    public void BothShortcutCombinationsRoundTrip(HotkeyModifiers modifiers)
    {
        var root = Path.Combine(Path.GetTempPath(), "ScreenshotDock.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(root);
            var settings = new AppSettings { Hotkey = new() { Modifiers = modifiers, VirtualKey = 0x71, KeyName = "F2" },
                PinHotkey = new() { Modifiers = modifiers, VirtualKey = 0x74, KeyName = "F5" } };
            store.Save(settings);
            Assert.Equal(settings.Hotkey, store.Load().Hotkey);
            Assert.Equal(settings.PinHotkey, store.Load().PinHotkey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DuplicateBindingIgnoresDisplayNames() => Assert.True(new HotkeySettings().SameBinding(new() { KeyName = "Function One" }));
}
