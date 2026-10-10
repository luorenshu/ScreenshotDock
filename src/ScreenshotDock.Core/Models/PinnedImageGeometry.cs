namespace ScreenshotDock.Core.Models;

[Flags]
public enum PinnedResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public static class PinnedImageGeometry
{
    public static PinnedResizeEdge HitTest(PixelRect bounds, PixelPoint point, int tolerance)
    {
        if (point.X < bounds.X - tolerance || point.X > bounds.Right + tolerance
            || point.Y < bounds.Y - tolerance || point.Y > bounds.Bottom + tolerance) return PinnedResizeEdge.None;
        var left = Math.Abs(point.X - bounds.X);
        var right = Math.Abs(point.X - bounds.Right);
        var top = Math.Abs(point.Y - bounds.Y);
        var bottom = Math.Abs(point.Y - bounds.Bottom);
        var horizontal = left <= right ? PinnedResizeEdge.Left : PinnedResizeEdge.Right;
        var vertical = top <= bottom ? PinnedResizeEdge.Top : PinnedResizeEdge.Bottom;
        if (Math.Min(left, right) <= tolerance * 2 && Math.Min(top, bottom) <= tolerance * 2) return horizontal | vertical;
        if (Math.Min(left, right) <= tolerance) return horizontal;
        if (Math.Min(top, bottom) <= tolerance) return vertical;
        return PinnedResizeEdge.None;
    }

    public static PixelRect Resize(PixelRect bounds, PinnedResizeEdge edges, PixelPoint delta, int minimumSide, double aspectRatio = 0)
    {
        if (edges == PinnedResizeEdge.None || bounds.IsEmpty) return bounds;
        var horizontal = edges.HasFlag(PinnedResizeEdge.Left) || edges.HasFlag(PinnedResizeEdge.Right);
        var vertical = edges.HasFlag(PinnedResizeEdge.Top) || edges.HasFlag(PinnedResizeEdge.Bottom);
        var dx = edges.HasFlag(PinnedResizeEdge.Left) ? -delta.X : delta.X;
        var dy = edges.HasFlag(PinnedResizeEdge.Top) ? -delta.Y : delta.Y;
        // Project corner movement onto the image diagonal so either axis can resize without distortion.
        var factor = horizontal && vertical
            ? 1 + (dx * (double)bounds.Width + dy * (double)bounds.Height) / ((double)bounds.Width * bounds.Width + (double)bounds.Height * bounds.Height)
            : horizontal ? 1 + dx / (double)bounds.Width : 1 + dy / (double)bounds.Height;
        factor = Math.Max(Math.Max(1, minimumSide) / (double)Math.Min(bounds.Width, bounds.Height), factor);
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0) aspectRatio = bounds.Width / (double)bounds.Height;
        var width = Math.Max((int)Math.Ceiling(Math.Max(1, minimumSide) * Math.Max(1, aspectRatio)), (int)Math.Round(bounds.Width * factor));
        var height = Math.Max(1, (int)Math.Round(width / aspectRatio));
        var x = edges.HasFlag(PinnedResizeEdge.Left) ? bounds.Right - width
            : horizontal ? bounds.X : bounds.X + (int)Math.Round((bounds.Width - width) / 2.0);
        var y = edges.HasFlag(PinnedResizeEdge.Top) ? bounds.Bottom - height
            : vertical ? bounds.Y : bounds.Y + (int)Math.Round((bounds.Height - height) / 2.0);
        return new(x, y, width, height);
    }
}
