using System.Windows.Media;
using ScreenshotDock.Core.Models;

namespace ScreenshotDock.App.Capture;

internal sealed record ArrowGeometry(Geometry? Fill, Geometry? Stroke)
{
    public static ArrowGeometry Create(ScreenshotAnnotation annotation)
    {
        var start = new Point(annotation.Start.X, annotation.Start.Y);
        var end = new Point(annotation.End.X, annotation.End.Y);
        var route = annotation.ArrowStyle == ArrowStyle.Polyline && !annotation.Points.IsDefaultOrEmpty
            ? Simplify(annotation.Points.Select(p => new Point(p.X, p.Y)).ToArray(), Math.Max(1, annotation.Thickness * 0.65))
            : [start, end];
        if (route.Count < 2) return new(null, null);
        start = route[0]; end = route[^1];
        var direction = end - route[^2];
        var length = direction.Length;
        if (length == 0) return new(null, null);
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        var headLength = Math.Min(length * 0.55, Math.Max(12, annotation.Thickness * 4.5));
        var headWidth = headLength * 0.48;
        var neck = end - direction * headLength;

        if (annotation.ArrowStyle is ArrowStyle.Line or ArrowStyle.Polyline)
            return new(null, Path(route, [neck + normal * headWidth, end, neck - normal * headWidth]));

        if (annotation.ArrowStyle == ArrowStyle.Solid)
            return new(Polygon([neck + normal * headWidth, end, neck - normal * headWidth]), Path([start, neck]));

        var shaftWidth = Math.Min(headWidth * 0.62, annotation.Thickness * (annotation.ArrowStyle == ArrowStyle.Outline ? 1.15 : 0.9));
        var points = new[] { start, neck + normal * shaftWidth, neck + normal * headWidth, end,
            neck - normal * headWidth, neck - normal * shaftWidth };
        var polygon = Polygon(points);
        return annotation.ArrowStyle == ArrowStyle.Outline ? new(null, polygon) : new(polygon, null);
    }

    private static Geometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], true, true);
            foreach (var point in points.Skip(1)) path.LineTo(point, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Path(params IReadOnlyList<Point>[] figures)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
            foreach (var points in figures)
            {
                path.BeginFigure(points[0], false, false);
                foreach (var point in points.Skip(1)) path.LineTo(point, true, false);
            }
        geometry.Freeze();
        return geometry;
    }

    // Keep turns while removing mouse jitter and tiny final segments that would shrink the head.
    private static IReadOnlyList<Point> Simplify(Point[] input, double tolerance)
    {
        var points = input.DistinctAdjacent().ToArray();
        if (points.Length <= 2) return points;
        var retained = new SortedSet<int> { 0, points.Length - 1 };
        var pending = new Stack<(int Start, int End)>();
        pending.Push((0, points.Length - 1));
        while (pending.TryPop(out var segment))
        {
            var vector = points[segment.End] - points[segment.Start];
            var squared = vector.LengthSquared;
            var furthest = -1;
            var distance = tolerance;
            for (var i = segment.Start + 1; i < segment.End; i++)
            {
                var offset = points[i] - points[segment.Start];
                var ratio = squared == 0 ? 0 : Math.Clamp(Vector.Multiply(offset, vector) / squared, 0, 1);
                var candidate = (offset - vector * ratio).Length;
                if (candidate > distance) { distance = candidate; furthest = i; }
            }
            if (furthest < 0) continue;
            retained.Add(furthest);
            pending.Push((segment.Start, furthest)); pending.Push((furthest, segment.End));
        }
        return retained.Select(i => points[i]).ToArray();
    }
}

internal static class ArrowPointExtensions
{
    public static IEnumerable<Point> DistinctAdjacent(this IEnumerable<Point> points)
    {
        Point? previous = null;
        foreach (var point in points)
            if (previous != point) { yield return point; previous = point; }
    }
}
