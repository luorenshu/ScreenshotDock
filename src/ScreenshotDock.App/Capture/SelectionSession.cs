using ScreenshotDock.Core.Models;

namespace ScreenshotDock.App.Capture;

[Flags]
public enum SelectionAction { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16, New = 32 }

public sealed class SelectionSession(PixelRect bounds)
{
    private PixelPoint _start;
    private PixelRect _original;
    public PixelRect Bounds { get; } = bounds;
    public PixelRect Selection { get; private set; }
    public SelectionAction Action { get; private set; }
    public event System.Action? Changed;

    public SelectionAction HitTest(PixelPoint p, int tolerance)
    {
        if (Selection.IsEmpty || p.X < Selection.X - tolerance || p.X > Selection.Right + tolerance
            || p.Y < Selection.Y - tolerance || p.Y > Selection.Bottom + tolerance) return SelectionAction.New;
        var result = SelectionAction.None;
        if (Math.Abs(p.X - Selection.X) <= tolerance) result |= SelectionAction.Left;
        else if (Math.Abs(p.X - Selection.Right) <= tolerance) result |= SelectionAction.Right;
        if (Math.Abs(p.Y - Selection.Y) <= tolerance) result |= SelectionAction.Top;
        else if (Math.Abs(p.Y - Selection.Bottom) <= tolerance) result |= SelectionAction.Bottom;
        return result == SelectionAction.None ? SelectionAction.Move : result;
    }

    public void Begin(PixelPoint point, int tolerance)
    {
        Action = HitTest(point, tolerance);
        _start = Bounds.Clamp(point);
        _original = Selection;
        if (Action == SelectionAction.New) Selection = new(_start.X, _start.Y, 0, 0);
        Changed?.Invoke();
    }

    public void Update(PixelPoint point)
    {
        var p = Bounds.Clamp(point);
        if (Action == SelectionAction.New) Selection = PixelRect.Between(_start, p);
        else if (Action == SelectionAction.Move)
            Selection = _original with { X = Math.Clamp(_original.X + p.X - _start.X, Bounds.X, Bounds.Right - _original.Width),
                Y = Math.Clamp(_original.Y + p.Y - _start.Y, Bounds.Y, Bounds.Bottom - _original.Height) };
        else if (Action != SelectionAction.None)
        {
            var x1 = Action.HasFlag(SelectionAction.Left) ? p.X : _original.X;
            var x2 = Action.HasFlag(SelectionAction.Right) ? p.X : _original.Right;
            var y1 = Action.HasFlag(SelectionAction.Top) ? p.Y : _original.Y;
            var y2 = Action.HasFlag(SelectionAction.Bottom) ? p.Y : _original.Bottom;
            Selection = PixelRect.Between(new(x1, y1), new(x2, y2));
        }
        Changed?.Invoke();
    }

    public void End() { Action = SelectionAction.None; Changed?.Invoke(); }
    public void SetSelection(PixelRect selection) { Selection = selection.Intersect(Bounds); Changed?.Invoke(); }
}
