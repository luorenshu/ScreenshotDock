using ScreenshotDock.Core.Models;

namespace ScreenshotDock.Core.Services;

public sealed class AnnotationDocument
{
    private readonly List<ScreenshotAnnotation> _items = [];
    private readonly Stack<ScreenshotAnnotation[]> _undo = [];
    private readonly Stack<ScreenshotAnnotation[]> _redo = [];
    public IReadOnlyList<ScreenshotAnnotation> Items => _items.AsReadOnly();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    public bool Add(ScreenshotAnnotation annotation)
    {
        if (!IsValid(annotation)) return false;
        Remember();
        _items.Add(annotation);
        Changed?.Invoke();
        return true;
    }

    public bool ReplaceAt(int index, ScreenshotAnnotation annotation)
    {
        if (index < 0 || index >= _items.Count || !IsValid(annotation)) return false;
        if (_items[index] == annotation) return true;
        Remember();
        _items[index] = annotation;
        Changed?.Invoke();
        return true;
    }

    private static bool IsValid(ScreenshotAnnotation annotation)
    {
        if (!double.IsFinite(annotation.Thickness) || annotation.Thickness <= 0
            || !double.IsFinite(annotation.FontSize) || annotation.FontSize <= 0
            || !double.IsFinite(annotation.TextWidth) || annotation.TextWidth < 0) return false;
        return annotation.Tool switch
        {
            CaptureTool.Rectangle or CaptureTool.Ellipse => annotation.Start.X != annotation.End.X && annotation.Start.Y != annotation.End.Y,
            CaptureTool.Mosaic => annotation.MosaicBlockSize is >= 2 and <= 512
                && annotation.Start.X != annotation.End.X && annotation.Start.Y != annotation.End.Y,
            CaptureTool.Arrow => Enum.IsDefined(annotation.ArrowStyle) && (annotation.ArrowStyle == ArrowStyle.Polyline
                ? !annotation.Points.IsDefaultOrEmpty && annotation.Points.Any(p => p != annotation.Points[0])
                : annotation.Start != annotation.End),
            CaptureTool.Pen => !annotation.Points.IsDefaultOrEmpty,
            CaptureTool.Text => !string.IsNullOrWhiteSpace(annotation.Text),
            _ => false
        };
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return false;
        Remember();
        _items.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    public void Reset()
    {
        _items.Clear();
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    private void Remember() { _undo.Push(_items.ToArray()); _redo.Clear(); }
    private bool Restore(Stack<ScreenshotAnnotation[]> from, Stack<ScreenshotAnnotation[]> to)
    {
        if (!from.TryPop(out var snapshot)) return false;
        to.Push(_items.ToArray());
        _items.Clear();
        _items.AddRange(snapshot);
        Changed?.Invoke();
        return true;
    }
}
