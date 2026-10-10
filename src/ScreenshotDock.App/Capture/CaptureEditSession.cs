using System.Collections.Immutable;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;

namespace ScreenshotDock.App.Capture;

public sealed class CaptureEditSession : IDisposable
{
    private readonly List<PixelPoint> _points = [];
    private ScreenshotAnnotation? _draft;
    private ScreenshotAnnotation? _editingRegion;
    private PixelPoint _regionEditStart;
    private PixelRect _regionResizeBounds;
    private PixelRect _lastSelection;
    public SelectionSession Selection { get; }
    public AnnotationDocument Document { get; } = new();
    public CaptureTool Tool { get; private set; }
    public uint ColorArgb { get; set; } = 0xFFFF4D4F;
    public double Thickness { get; set; } = 5;
    public double FontSize { get; set; } = 20;
    public double MosaicSize { get; set; } = 16;
    public int? EditingTextIndex { get; private set; }
    public ScreenshotAnnotation? EditingText { get; private set; }
    public ArrowStyle ArrowStyle { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsDrawing => _draft is not null;
    public bool IsEditingRegion => _editingRegion is not null;
    public SelectionAction RegionAction { get; private set; }
    public int? SelectedRegionIndex { get; private set; }
    public ScreenshotAnnotation? RegionPreview { get; private set; }
    public ScreenshotAnnotation? SelectedRegion => RegionPreview ?? (SelectedRegionIndex is { } index
        && index < Document.Items.Count ? Document.Items[index] : null);
    public string Status { get; private set; } = "";
    public ScreenshotAnnotation? Draft => _draft is not null && UsesPath(_draft)
        ? _draft with { Points = _points.ToImmutableArray() } : _draft;
    public event Action? Changed;
    public event Action? CommitTextRequested;
    public event Action<PixelPoint>? TextRequested;

    public CaptureEditSession(SelectionSession selection)
    {
        Selection = selection;
        _lastSelection = selection.Selection;
        Selection.Changed += SelectionChanged;
        Document.Changed += DocumentChanged;
    }

    public void SetTool(CaptureTool tool)
    {
        CommitText();
        CancelStroke();
        CancelRegionEdit();
        SelectedRegionIndex = null;
        Tool = tool;
        Status = "";
        Refresh();
    }
    public void SetBusy(bool busy) { IsBusy = busy; Refresh(); }
    public void SetArrowStyle(ArrowStyle style)
    {
        if (!Enum.IsDefined(style)) return;
        ArrowStyle = style;
        Status = "";
        Refresh();
    }
    public void SetStatus(string status) { Status = status; Refresh(); }
    public void CommitText() => CommitTextRequested?.Invoke();
    public void CancelTextEdit() { EditingTextIndex = null; EditingText = null; Refresh(); }
    public void SaveText(ScreenshotAnnotation annotation)
    {
        var index = EditingTextIndex;
        var original = EditingText;
        EditingTextIndex = null;
        EditingText = null;
        if (index.HasValue)
        {
            if (index.Value < Document.Items.Count && Document.Items[index.Value] == original)
            {
                if (string.IsNullOrWhiteSpace(annotation.Text)) Document.RemoveAt(index.Value);
                else Document.ReplaceAt(index.Value, annotation);
            }
        }
        else Document.Add(annotation);
        Refresh();
    }

    public bool TryEditText(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || !Selection.Selection.Contains(screenPoint)) return false;
        CommitText();
        var point = Relative(screenPoint);
        for (var i = Document.Items.Count - 1; i >= 0; i--)
        {
            var annotation = Document.Items[i];
            if (annotation.Tool != CaptureTool.Text || !AnnotationRenderer.HitTest(annotation, point, 3 * scale)) continue;
            CancelStroke();
            CancelRegionEdit();
            SelectedRegionIndex = null;
            EditingTextIndex = i;
            EditingText = annotation;
            Tool = CaptureTool.Text;
            Status = "";
            TextRequested?.Invoke(new(Selection.Selection.X + annotation.Start.X, Selection.Selection.Y + annotation.Start.Y));
            Refresh();
            return true;
        }
        return false;
    }
    public void NotifyStyleChanged() => Refresh();

    public int? HitTestRegion(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || !Selection.Selection.Contains(screenPoint)) return null;
        var point = Relative(screenPoint);
        for (var i = Document.Items.Count - 1; i >= 0; i--)
            if (CanEditRegion(Document.Items[i]) && AnnotationRenderer.HitTestRegion(Document.Items[i], point, 3 * scale)) return i;
        return null;
    }

    private static bool IsRegionTool(CaptureTool tool) => tool is CaptureTool.Rectangle or CaptureTool.Ellipse or CaptureTool.Mosaic;
    private bool CanEditRegion(ScreenshotAnnotation annotation) => IsRegionTool(annotation.Tool)
        && (Tool == CaptureTool.Select || Tool == annotation.Tool);

    public bool TryBeginRegionMove(PixelPoint screenPoint, double scale)
    {
        if (IsDrawing || IsEditingRegion || Selection.Action != SelectionAction.None) return false;
        CommitText();
        if (HitTestRegion(screenPoint, scale) is not { } index) return false;
        BeginRegionEdit(index, SelectionAction.Move, screenPoint);
        return true;
    }

    public SelectionAction HitTestRegionHandle(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || SelectedRegion is not { } annotation || !CanEditRegion(annotation))
            return SelectionAction.None;
        var bounds = PixelRect.Between(annotation.Start, annotation.End);
        if (bounds.Intersect(new(0, 0, Selection.Selection.Width, Selection.Selection.Height)).IsEmpty) return SelectionAction.None;
        return RegionSelectionGeometry.HitTestHandle(bounds,
            new(screenPoint.X - Selection.Selection.X, screenPoint.Y - Selection.Selection.Y), 5 * scale, annotation.Tool);
    }

    public bool TryBeginRegionResize(PixelPoint screenPoint, double scale)
    {
        if (IsDrawing || IsEditingRegion || Selection.Action != SelectionAction.None) return false;
        CommitText();
        var action = HitTestRegionHandle(screenPoint, scale);
        if (action == SelectionAction.None || SelectedRegionIndex is not { } index) return false;
        BeginRegionEdit(index, action, screenPoint);
        return true;
    }

    private void BeginRegionEdit(int index, SelectionAction action, PixelPoint screenPoint)
    {
        SelectedRegionIndex = index;
        _editingRegion = Document.Items[index];
        RegionPreview = _editingRegion;
        _regionEditStart = screenPoint;
        _regionResizeBounds = PixelRect.Between(_editingRegion.Start, _editingRegion.End)
            .Intersect(new(0, 0, Selection.Selection.Width, Selection.Selection.Height));
        RegionAction = action;
        Status = "";
        Refresh();
    }

    public void UpdateRegionEdit(PixelPoint screenPoint)
    {
        if (_editingRegion is not { } original) return;
        if (RegionAction != SelectionAction.Move)
        {
            UpdateRegionResize(original, screenPoint);
            Refresh();
            return;
        }
        var bounds = PixelRect.Between(original.Start, original.End);
        var selection = Selection.Selection;
        // A previously resized capture may clip a larger annotation; keep its size and allow its visible part to move.
        var x1 = -bounds.X; var x2 = selection.Width - bounds.Right;
        var y1 = -bounds.Y; var y2 = selection.Height - bounds.Bottom;
        var dx = ClampRegionOffset(screenPoint.X - _regionEditStart.X, x1, x2);
        var dy = ClampRegionOffset(screenPoint.Y - _regionEditStart.Y, y1, y2);
        RegionPreview = original with { Start = new(original.Start.X + dx, original.Start.Y + dy),
            End = new(original.End.X + dx, original.End.Y + dy) };
        Refresh();
    }

    private void UpdateRegionResize(ScreenshotAnnotation original, PixelPoint screenPoint)
    {
        var bounds = _regionResizeBounds;
        var selection = Selection.Selection;
        var dx = screenPoint.X - _regionEditStart.X;
        var dy = screenPoint.Y - _regionEditStart.Y;
        var left = bounds.X; var right = bounds.Right;
        var top = bounds.Y; var bottom = bounds.Bottom;
        if (RegionAction.HasFlag(SelectionAction.Left)) left = Math.Clamp(left + dx, 0, right - 1);
        if (RegionAction.HasFlag(SelectionAction.Right)) right = Math.Clamp(right + dx, left + 1, selection.Width);
        if (RegionAction.HasFlag(SelectionAction.Top)) top = Math.Clamp(top + dy, 0, bottom - 1);
        if (RegionAction.HasFlag(SelectionAction.Bottom)) bottom = Math.Clamp(bottom + dy, top + 1, selection.Height);
        RegionPreview = original with
        {
            Start = new(original.Start.X > original.End.X ? right : left, original.Start.Y > original.End.Y ? bottom : top),
            End = new(original.Start.X > original.End.X ? left : right, original.Start.Y > original.End.Y ? top : bottom)
        };
    }

    public void EndRegionEdit()
    {
        var index = SelectedRegionIndex;
        var original = _editingRegion;
        var preview = RegionPreview;
        _editingRegion = null;
        RegionPreview = null;
        RegionAction = SelectionAction.None;
        if (index is { } i && original is not null && preview is not null && i < Document.Items.Count
            && Document.Items[i] == original && Document.ReplaceAt(i, preview)) SelectedRegionIndex = i;
        Refresh();
    }

    public void CancelRegionEdit() { ClearRegionEdit(); Refresh(); }
    private void ClearRegionEdit() { _editingRegion = null; RegionPreview = null; RegionAction = SelectionAction.None; }
    private static int ClampRegionOffset(int offset, int minimum, int maximum) => minimum <= maximum
        ? Math.Clamp(offset, minimum, maximum) : Math.Clamp(offset, Math.Min(maximum, 0), Math.Max(minimum, 0));

    public PixelPoint Relative(PixelPoint screenPoint)
    {
        var bounded = Selection.Selection.Clamp(screenPoint);
        return new(bounded.X - Selection.Selection.X, bounded.Y - Selection.Selection.Y);
    }

    public void BeginSelection(PixelPoint point, int tolerance)
    {
        CommitText();
        CancelStroke();
        CancelRegionEdit();
        SelectedRegionIndex = null;
        if (Selection.HitTest(point, tolerance) == SelectionAction.New) Document.Reset();
        Selection.Begin(point, tolerance);
    }

    public void BeginStroke(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || Selection.Selection.IsEmpty || !Selection.Selection.Contains(screenPoint)) return;
        CommitText();
        CancelRegionEdit();
        SelectedRegionIndex = null;
        var point = Relative(screenPoint);
        if (Tool == CaptureTool.Text)
        {
            if (!TryEditText(screenPoint, scale)) TextRequested?.Invoke(screenPoint);
            return;
        }
        if (Tool == CaptureTool.Eraser)
        {
            for (var i = Document.Items.Count - 1; i >= 0; i--)
                if (AnnotationRenderer.HitTest(Document.Items[i], point, 6 * scale)) { Document.RemoveAt(i); break; }
            return;
        }
        if (Tool == CaptureTool.Select) return;
        _points.Clear();
        _points.Add(point);
        _draft = new(Tool, point, point, ColorArgb, Thickness * scale, FontSize * scale, ArrowStyle: ArrowStyle,
            MosaicBlockSize: Math.Clamp((int)Math.Round(MosaicSize * scale), 2, 512));
        Refresh();
    }

    public void UpdateStroke(PixelPoint screenPoint)
    {
        if (_draft is null) return;
        var point = Relative(screenPoint);
        _draft = _draft with { End = point };
        if (UsesPath(_draft) && _points[^1] != point) _points.Add(point);
        Refresh();
    }
    public void EndStroke()
    {
        var draft = Draft;
        _draft = null;
        _points.Clear();
        if (draft is not null && Document.Add(draft) && IsRegionTool(draft.Tool))
            SelectedRegionIndex = Document.Items.Count - 1;
        Refresh();
    }
    public void CancelStroke() { _draft = null; _points.Clear(); Refresh(); }
    private static bool UsesPath(ScreenshotAnnotation annotation) => annotation.Tool == CaptureTool.Pen
        || annotation.Tool == CaptureTool.Arrow && annotation.ArrowStyle == ArrowStyle.Polyline;
    private void DocumentChanged()
    {
        ClearRegionEdit();
        SelectedRegionIndex = null;
        Refresh();
    }
    private void SelectionChanged()
    {
        if (_lastSelection != Selection.Selection)
        {
            _lastSelection = Selection.Selection;
            ClearRegionEdit();
            SelectedRegionIndex = null;
        }
        Refresh();
    }
    private void Refresh() => Changed?.Invoke();
    public void Dispose() { Selection.Changed -= SelectionChanged; Document.Changed -= DocumentChanged; }
}
