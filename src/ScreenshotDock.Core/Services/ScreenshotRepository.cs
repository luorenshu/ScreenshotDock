using System.Buffers.Binary;
using System.Security.Cryptography;
using ScreenshotDock.Core.Models;

namespace ScreenshotDock.Core.Services;

public sealed class ScreenshotRepository
{
    private readonly object _gate = new();
    private readonly List<ScreenshotItem> _items = [];
    private readonly string _indexPath;
    public string Root { get; }
    public string ScreenshotDirectory { get; }
    public string ThumbnailDirectory { get; }
    public Action<Exception>? OnError { get; set; }

    public ScreenshotRepository(string root)
    {
        Root = Path.GetFullPath(root);
        ScreenshotDirectory = Path.Combine(Root, "screenshots");
        ThumbnailDirectory = Path.Combine(Root, "thumbnails");
        _indexPath = Path.Combine(Root, "screenshots.json");
        Directory.CreateDirectory(ScreenshotDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
    }

    public string GetImagePath(Guid id) => Path.Combine(ScreenshotDirectory, $"{id:D}.png");
    public string GetThumbnailPath(Guid id) => Path.Combine(ThumbnailDirectory, $"{id:D}.png");
    public IReadOnlyList<ScreenshotItem> Items { get { lock (_gate) return _items.OrderByDescending(i => i.CreatedAt).ToArray(); } }

    public void Load(StorageSettings settings, DateTimeOffset now)
    {
        lock (_gate)
        {
            var index = AtomicJson.Read<List<ScreenshotItem>>(_indexPath)
                ?? AtomicJson.Read<List<ScreenshotItem>>(_indexPath + ".bak") ?? [];
            var known = index.Where(i => i is not null).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            _items.Clear();
            // Reconcile against owned files. Never trust a file path from imported JSON.
            foreach (var path in Directory.EnumerateFiles(ScreenshotDirectory, "*.png"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var (width, height) = ReadPngSize(bytes);
                    var created = known.TryGetValue(id, out var previous) ? previous.CreatedAt : new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                    _items.Add(new ScreenshotItem { Id = id, CreatedAt = created, Width = width, Height = height,
                        FileSize = bytes.Length, ContentHash = Convert.ToHexString(SHA256.HashData(bytes)) });
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { OnError?.Invoke(e); }
            }
            CleanupCore(settings, now);
            Persist();
        }
    }

    public AddScreenshotResult Add(byte[] png, byte[] thumbnail, StorageSettings settings, DateTimeOffset now)
    {
        var (width, height) = ReadPngSize(png);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i => i.ContentHash == hash && File.Exists(GetImagePath(i.Id)));
            if (existing is not null)
            {
                var updated = existing with { CreatedAt = now };
                _items[_items.IndexOf(existing)] = updated;
                CleanupCore(settings, now);
                Persist();
                return new(updated, true);
            }
            var item = new ScreenshotItem { Id = Guid.NewGuid(), CreatedAt = now, Width = width,
                Height = height, FileSize = png.Length, ContentHash = hash };
            WriteImage(GetImagePath(item.Id), png);
            WriteImage(GetThumbnailPath(item.Id), thumbnail);
            _items.Add(item);
            CleanupCore(settings, now);
            Persist();
            return new(item, false);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate) { DeleteCore(id); Persist(); }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var id in _items.Select(i => i.Id).ToArray()) DeleteCore(id);
            Persist();
        }
    }

    public void Cleanup(StorageSettings settings, DateTimeOffset now)
    {
        lock (_gate) { CleanupCore(settings, now); Persist(); }
    }

    private void CleanupCore(StorageSettings settings, DateTimeOffset now)
    {
        if (!settings.AutoCleanup) return;
        var ordered = _items.OrderByDescending(i => i.CreatedAt).ToArray();
        var expired = ordered.Where((i, position) => position >= settings.MaxItems
            || i.CreatedAt < now.AddDays(-settings.RetentionDays)).ToArray();
        foreach (var item in expired)
        {
            try { DeleteCore(item.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { OnError?.Invoke(e); }
        }
    }

    private void DeleteCore(Guid id)
    {
        // Only the app-owned GUID paths are eligible for deletion. Saved copies are outside this store.
        File.Delete(GetImagePath(id));
        try { File.Delete(GetThumbnailPath(id)); }
        catch (IOException e) { OnError?.Invoke(e); }
        _items.RemoveAll(i => i.Id == id);
    }

    private void Persist() => AtomicJson.Write(_indexPath, _items.OrderByDescending(i => i.CreatedAt).ToArray());

    private static void WriteImage(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("无效的 PNG 图片。");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535) throw new InvalidDataException("PNG 尺寸无效。");
        return (width, height);
    }
}
