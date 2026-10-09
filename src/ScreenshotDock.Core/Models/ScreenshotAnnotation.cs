using System.Collections.Immutable;

namespace ScreenshotDock.Core.Models;

public enum CaptureTool { Select, Rectangle, Ellipse, Arrow, Pen, Text, Eraser, Mosaic }
public enum ArrowStyle { Line, Solid, Tapered, Outline, Polyline }

// Annotation coordinates and sizes are physical pixels relative to the selected image.
public sealed record ScreenshotAnnotation(
    CaptureTool Tool, PixelPoint Start, PixelPoint End, uint ColorArgb, double Thickness,
    double FontSize = 20, string Text = "", ImmutableArray<PixelPoint> Points = default, double TextWidth = 0,
    ArrowStyle ArrowStyle = ArrowStyle.Line, int MosaicBlockSize = 16);
