namespace ScreenshotDock.Core.Models;

public readonly record struct PixelPoint(int X, int Y);

// All selection and desktop coordinates are physical pixels, including negative monitor origins.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public static PixelRect Between(PixelPoint a, PixelPoint b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public PixelPoint Clamp(PixelPoint p) => new(Math.Clamp(p.X, X, Right), Math.Clamp(p.Y, Y, Bottom));
}
