using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using Xunit;

namespace ScreenshotDock.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ScreenshotDock.Tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1kAAAAASUVORK5CYII=");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private readonly ScreenshotRepository _repository;
    public StorageTests() => _repository = new(_root);

    [Fact]
    public void RestartRestoresImageAndMetadata()
    {
        var added = _repository.Add(Png, Png, new(), Now);
        var restored = new ScreenshotRepository(_root);
        restored.Load(new(), Now.AddHours(1));
        Assert.Equal(added.Item.Id, Assert.Single(restored.Items).Id);
        Assert.Equal(Png, File.ReadAllBytes(restored.GetImagePath(added.Item.Id)));
    }

    [Fact]
    public void DuplicateIsPromotedWithoutAnotherFile()
    {
        var first = _repository.Add(Png, Png, new(), Now);
        var second = _repository.Add(Png, Png, new(), Now.AddHours(2));
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Item.Id, second.Item.Id);
        Assert.Equal(Now.AddHours(2), Assert.Single(_repository.Items).CreatedAt);
        Assert.Single(Directory.GetFiles(_repository.ScreenshotDirectory));
    }

    [Fact]
    public void CleanupPreservesExternalSavedCopy()
    {
        var item = _repository.Add(Png, Png, new(), Now);
        var saved = Path.Combine(_root, "user-saved.png");
        File.Copy(_repository.GetImagePath(item.Item.Id), saved);
        _repository.Cleanup(new(), Now.AddDays(8));
        Assert.Empty(_repository.Items);
        Assert.False(File.Exists(_repository.GetImagePath(item.Item.Id)));
        Assert.Equal(Png, File.ReadAllBytes(saved));
    }

    [Fact]
    public void OrphanIsRecoveredAfterInterruptedIndexWrite()
    {
        var id = Guid.NewGuid();
        File.WriteAllBytes(_repository.GetImagePath(id), Png);
        File.SetLastWriteTimeUtc(_repository.GetImagePath(id), Now.UtcDateTime);
        File.WriteAllText(Path.Combine(_root, "screenshots.json"), "{broken");
        _repository.Load(new(), Now);
        Assert.Equal(id, Assert.Single(_repository.Items).Id);
    }

    [Fact]
    public void CorruptIndexUsesBackupTimestamps()
    {
        var item = _repository.Add(Png, Png, new(), Now);
        _repository.Cleanup(new(), Now);
        File.WriteAllText(Path.Combine(_root, "screenshots.json"), "[");
        _repository.Load(new(), Now);
        Assert.Equal(Now, Assert.Single(_repository.Items).CreatedAt);
        Assert.Equal(item.Item.Id, _repository.Items[0].Id);
    }

    [Fact]
    public void MissingOriginalDoesNotSurviveRestart()
    {
        var item = _repository.Add(Png, Png, new(), Now);
        File.Delete(_repository.GetImagePath(item.Item.Id));
        _repository.Load(new(), Now);
        Assert.Empty(_repository.Items);
    }

    [Fact]
    public void QuantityLimitKeepsNewest()
    {
        var first = _repository.Add(Png, Png, new(), Now);
        var different = (byte[])Png.Clone();
        different[^1] ^= 1; // Different content, same dimensions; repository treats image bytes as opaque.
        var second = _repository.Add(different, different, new() { MaxItems = 1 }, Now.AddMinutes(1));
        Assert.Equal(second.Item.Id, Assert.Single(_repository.Items).Id);
        Assert.False(File.Exists(_repository.GetImagePath(first.Item.Id)));
    }

    [Fact]
    public void DisabledCleanupKeepsExpiredImages()
    {
        _repository.Add(Png, Png, new(), Now);
        _repository.Cleanup(new() { AutoCleanup = false }, Now.AddDays(100));
        Assert.Single(_repository.Items);
    }

    [Fact]
    public void UnownedFilesAreNeverImportedOrDeleted()
    {
        var path = Path.Combine(_repository.ScreenshotDirectory, "precious.png");
        File.WriteAllBytes(path, Png);
        _repository.Load(new(), Now);
        _repository.Clear();
        Assert.Empty(_repository.Items);
        Assert.Equal(Png, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidImageIsRejectedBeforeWriting()
    {
        Assert.Throws<InvalidDataException>(() => _repository.Add([1, 2, 3], Png, new(), Now));
        Assert.Empty(Directory.GetFiles(_repository.ScreenshotDirectory));
    }

    [Fact]
    public void SettingsFallbackAndValidationWork()
    {
        var store = new SettingsStore(_root);
        store.Save(new() { Storage = new() { MaxItems = -5, RetentionDays = 9999 } });
        Assert.Equal(1, store.Load().Storage.MaxItems);
        Assert.Equal(365, store.Load().Storage.RetentionDays);
        store.Save(new());
        File.WriteAllText(store.FilePath, "broken");
        Assert.Equal(1, store.Load().Storage.MaxItems);
    }

    [Fact]
    public void InvalidHotkeyRestoresDefault()
    {
        var settings = new AppSettings { Hotkey = new() { Modifiers = HotkeyModifiers.None, VirtualKey = 0 } }.Normalize();
        Assert.Equal("F1", settings.Hotkey.Display);
    }

    [Fact]
    public void DeleteDoesNotResurrectFromBackup()
    {
        var item = _repository.Add(Png, Png, new(), Now);
        _repository.Delete(item.Item.Id);
        var restored = new ScreenshotRepository(_root);
        restored.Load(new(), Now);
        Assert.Empty(restored.Items);
    }

    [Theory]
    [InlineData(-1920, -200, 100, 100)]
    [InlineData(3000, 2000, 50, 70)]
    public void PhysicalCoordinatesPreserveNegativeMonitorOrigins(int x, int y, int width, int height)
    {
        var rect = PixelRect.Between(new(x + width, y + height), new(x, y));
        Assert.Equal(new PixelRect(x, y, width, height), rect);
        Assert.Equal(new PixelRect(x, y, width / 2, height), rect.Intersect(new(x - 10, y, width / 2 + 10, height)));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
