using ScreenshotDock.Core.Models;

namespace ScreenshotDock.Core.Services;

public sealed class SettingsStore(string root)
{
    public string FilePath { get; } = Path.Combine(root, "settings.json");
    public AppSettings Load() => (AtomicJson.Read<AppSettings>(FilePath)
        ?? AtomicJson.Read<AppSettings>(FilePath + ".bak") ?? new()).Normalize();
    public void Save(AppSettings settings) => AtomicJson.Write(FilePath, settings.Normalize());
}
