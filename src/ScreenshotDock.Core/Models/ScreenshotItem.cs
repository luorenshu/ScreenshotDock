namespace ScreenshotDock.Core.Models;

public sealed record ScreenshotItem
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long FileSize { get; init; }
    public string ContentHash { get; init; } = "";
}

public sealed record AddScreenshotResult(ScreenshotItem Item, bool IsDuplicate);
