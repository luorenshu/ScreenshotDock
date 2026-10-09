using ScreenshotDock.Core.Models;

namespace ScreenshotDock.App.Capture;

internal static class MosaicSelectionGeometry
{
    public static IReadOnlyList<(PixelPoint Point, SelectionAction Action)> Handles(PixelRect bounds) =>
    [
        (new(bounds.X, bounds.Y), SelectionAction.Left | SelectionAction.Top),
        (new(bounds.Right, bounds.Y), SelectionAction.Right | SelectionAction.Top),
        (new(bounds.X, bounds.Bottom), SelectionAction.Left | SelectionAction.Bottom),
        (new(bounds.Right, bounds.Bottom), SelectionAction.Right | SelectionAction.Bottom),
        (new(bounds.X + bounds.Width / 2, bounds.Y), SelectionAction.Top),
        (new(bounds.X + bounds.Width / 2, bounds.Bottom), SelectionAction.Bottom),
        (new(bounds.X, bounds.Y + bounds.Height / 2), SelectionAction.Left),
        (new(bounds.Right, bounds.Y + bounds.Height / 2), SelectionAction.Right)
    ];

    public static SelectionAction HitTestHandle(PixelRect bounds, PixelPoint point, double tolerance) => Handles(bounds)
        .Where(h => Math.Abs(h.Point.X - point.X) <= tolerance && Math.Abs(h.Point.Y - point.Y) <= tolerance)
        .OrderBy(h => (double)(h.Point.X - point.X) * (h.Point.X - point.X) + (double)(h.Point.Y - point.Y) * (h.Point.Y - point.Y))
        .Select(h => h.Action).FirstOrDefault();
}
