using System.Windows.Input;

namespace ScreenshotDock.App.Capture;

public static class CaptureCursors
{
    public static Cursor Crosshair { get; } = Load("crosshair");
    public static Cursor Text { get; } = Load("text");
    public static Cursor Move { get; } = Load("move");
    public static Cursor Horizontal { get; } = Load("resize-horizontal");
    public static Cursor Vertical { get; } = Load("resize-vertical");
    public static Cursor DiagonalDown { get; } = Load("resize-diagonal-down");
    public static Cursor DiagonalUp { get; } = Load("resize-diagonal-up");

    private static Cursor Load(string name)
    {
        using var stream = typeof(CaptureCursors).Assembly.GetManifestResourceStream($"ScreenshotDock.Cursors.{name}.cur")
            ?? throw new InvalidOperationException($"缺少截图光标资源：{name}");
        return new Cursor(stream, scaleWithDpi: true);
    }
}
