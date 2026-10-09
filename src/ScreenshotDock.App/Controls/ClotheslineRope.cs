using System.Windows.Media;

namespace ScreenshotDock.App.Controls;

public sealed class ClotheslineRope : FrameworkElement
{
    public static double HeightAt(double x, double width)
    {
        var t = Math.Clamp(x / Math.Max(1, width), 0, 1);
        return 22 + 70 * t * (1 - t);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var curve = new StreamGeometry();
        using (var path = curve.Open())
        {
            path.BeginFigure(new Point(0, 22), false, false);
            path.BezierTo(new Point(ActualWidth / 3, 45.333), new Point(ActualWidth * 2 / 3, 45.333), new Point(ActualWidth, 22), true, false);
        }
        curve.Freeze();
        drawing.PushTransform(new TranslateTransform(0, 1.5));
        drawing.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(85, 12, 27, 52)), 2.5), curve);
        drawing.Pop();
        drawing.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(190, 187, 213, 246)), 1.2), curve);
    }
}
