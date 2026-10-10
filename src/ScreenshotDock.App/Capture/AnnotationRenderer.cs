using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.App.Capture;

public static class AnnotationRenderer
{
    private static Point Point(PixelPoint p) => new(p.X, p.Y);
    private static Brush Brush(ScreenshotAnnotation a) => new SolidColorBrush(Color.FromArgb(
        (byte)(a.ColorArgb >> 24), (byte)(a.ColorArgb >> 16), (byte)(a.ColorArgb >> 8), (byte)a.ColorArgb));

    private static FormattedText Text(ScreenshotAnnotation a)
    {
        var text = new FormattedText(a.Text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), a.FontSize, Brush(a), 1);
        if (a.TextWidth > 0) text.MaxTextWidth = a.TextWidth;
        return text;
    }

    private static Geometry Geometry(ScreenshotAnnotation a)
    {
        var bounds = new Rect(Point(a.Start), Point(a.End));
        if (a.Tool == CaptureTool.Rectangle) return new RectangleGeometry(bounds);
        if (a.Tool == CaptureTool.Ellipse) return new EllipseGeometry(bounds);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            if (a.Tool == CaptureTool.Pen && !a.Points.IsDefaultOrEmpty)
            {
                path.BeginFigure(Point(a.Points[0]), false, false);
                foreach (var point in a.Points.Skip(1)) path.LineTo(Point(point), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public static void Draw(DrawingContext drawing, ScreenshotAnnotation annotation, BitmapSource? source = null)
    {
        if (annotation.Tool == CaptureTool.Mosaic)
        {
            if (source is null) throw new ArgumentException("马赛克需要原始截图。", nameof(source));
            var bounds = new Rect(Point(annotation.Start), Point(annotation.End));
            bounds.Intersect(new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
            var grid = MosaicRenderer.Grid(source, annotation.MosaicBlockSize);
            var pixels = new DrawingGroup { ClipGeometry = new RectangleGeometry(bounds) };
            // Put the sampling hint on the drawing itself; bitmap export does not inherit a visual's hint.
            RenderOptions.SetBitmapScalingMode(pixels, BitmapScalingMode.NearestNeighbor);
            using (var context = pixels.Open())
                context.DrawImage(grid, new Rect(0, 0, grid.PixelWidth * annotation.MosaicBlockSize, grid.PixelHeight * annotation.MosaicBlockSize));
            pixels.Freeze();
            drawing.DrawDrawing(pixels);
        }
        else if (annotation.Tool == CaptureTool.Arrow)
        {
            var shape = ArrowGeometry.Create(annotation);
            var brush = Brush(annotation);
            if (shape.Fill is not null) drawing.DrawGeometry(brush, null, shape.Fill);
            if (shape.Stroke is not null)
            {
                var pen = Stroke(brush, annotation.Thickness);
                if (annotation.ArrowStyle == ArrowStyle.Solid) pen.EndLineCap = PenLineCap.Flat;
                drawing.DrawGeometry(null, pen, shape.Stroke);
            }
        }
        else if (annotation.Tool == CaptureTool.Text) drawing.DrawText(Text(annotation), Point(annotation.Start));
        else if (annotation.Tool == CaptureTool.Pen && annotation.Points.Length == 1)
            drawing.DrawEllipse(Brush(annotation), null, Point(annotation.Start), annotation.Thickness / 2, annotation.Thickness / 2);
        else
        {
            drawing.DrawGeometry(null, Stroke(Brush(annotation), annotation.Thickness), Geometry(annotation));
        }
    }

    private static Pen Stroke(Brush brush, double thickness) => new(brush, thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    public static bool HitTest(ScreenshotAnnotation annotation, PixelPoint point, double tolerance)
    {
        if (annotation.Tool == CaptureTool.Mosaic)
        {
            var bounds = new Rect(Point(annotation.Start), Point(annotation.End));
            bounds.Inflate(tolerance, tolerance);
            return bounds.Contains(Point(point));
        }
        if (annotation.Tool == CaptureTool.Arrow)
        {
            var shape = ArrowGeometry.Create(annotation);
            var location = Point(point);
            return shape.Fill?.FillContains(location) == true
                || tolerance > 0 && shape.Fill?.StrokeContains(Stroke(Brushes.Black, tolerance * 2), location) == true
                || shape.Stroke?.StrokeContains(Stroke(Brushes.Black, annotation.Thickness + tolerance * 2), location) == true;
        }
        if (annotation.Tool == CaptureTool.Text)
        {
            var text = Text(annotation);
            var bounds = new Rect(Point(annotation.Start), new Size(text.WidthIncludingTrailingWhitespace, text.Height));
            bounds.Inflate(tolerance, tolerance);
            return bounds.Contains(Point(point));
        }
        if (annotation.Tool == CaptureTool.Pen && annotation.Points.Length == 1)
            return (Point(annotation.Start) - Point(point)).Length <= annotation.Thickness / 2 + tolerance;
        return Geometry(annotation).StrokeContains(new Pen(Brushes.Black, annotation.Thickness + tolerance * 2), Point(point));
    }

    public static bool HitTestRegion(ScreenshotAnnotation annotation, PixelPoint point, double tolerance) =>
        annotation.Tool == CaptureTool.Mosaic ? HitTest(annotation, point, tolerance)
            : annotation.Tool is CaptureTool.Rectangle or CaptureTool.Ellipse
                && (Geometry(annotation).FillContains(Point(point)) || HitTest(annotation, point, tolerance));

    public static BitmapSource Render(DesktopFrame frame, PixelRect selection, IEnumerable<ScreenshotAnnotation> annotations)
    {
        var crop = ScreenCaptureService.Crop(frame, selection);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, crop.PixelWidth, crop.PixelHeight);
            context.PushClip(new RectangleGeometry(bounds));
            context.DrawImage(crop, bounds);
            foreach (var annotation in annotations) Draw(context, annotation, crop);
            context.Pop();
        }
        var image = new RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        image.Freeze();
        return image;
    }
}
