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
}
