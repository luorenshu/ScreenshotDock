using System.Collections.Immutable;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;

namespace ScreenshotDock.App.Capture;

public sealed class CaptureEditSession : IDisposable
{
    private readonly List<PixelPoint> _points = [];
    private ScreenshotAnnotation? _draft;
    private ScreenshotAnnotation? _movingMosaic;
    private PixelPoint _mosaicMoveStart;
    private PixelRect _mosaicResizeBounds;
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
    public bool IsMovingMosaic => _movingMosaic is not null;
    public SelectionAction MosaicAction { get; private set; }
    public int? SelectedMosaicIndex { get; private set; }
    public ScreenshotAnnotation? MosaicMovePreview { get; private set; }
    public ScreenshotAnnotation? SelectedMosaic => MosaicMovePreview ?? (SelectedMosaicIndex is { } index
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
        CancelMosaicMove();
        SelectedMosaicIndex = null;
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
            CancelMosaicMove();
            SelectedMosaicIndex = null;
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

    public int? HitTestMosaic(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || Tool is not (CaptureTool.Select or CaptureTool.Mosaic)
            || !Selection.Selection.Contains(screenPoint)) return null;
        var point = Relative(screenPoint);
        for (var i = Document.Items.Count - 1; i >= 0; i--)
            if (Document.Items[i].Tool == CaptureTool.Mosaic && AnnotationRenderer.HitTest(Document.Items[i], point, 3 * scale)) return i;
        return null;
    }

    public bool TryBeginMosaicMove(PixelPoint screenPoint, double scale)
    {
        if (IsDrawing || IsMovingMosaic || Selection.Action != SelectionAction.None) return false;
        CommitText();
        if (HitTestMosaic(screenPoint, scale) is not { } index) return false;
        BeginMosaicEdit(index, SelectionAction.Move, screenPoint);
        return true;
    }

    public SelectionAction HitTestMosaicHandle(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || Tool is not (CaptureTool.Select or CaptureTool.Mosaic) || SelectedMosaic is not { } annotation)
            return SelectionAction.None;
        var bounds = PixelRect.Between(annotation.Start, annotation.End);
        if (bounds.Intersect(new(0, 0, Selection.Selection.Width, Selection.Selection.Height)).IsEmpty) return SelectionAction.None;
        return MosaicSelectionGeometry.HitTestHandle(bounds,
            new(screenPoint.X - Selection.Selection.X, screenPoint.Y - Selection.Selection.Y), 5 * scale);
    }

    public bool TryBeginMosaicResize(PixelPoint screenPoint, double scale)
    {
        if (IsDrawing || IsMovingMosaic || Selection.Action != SelectionAction.None) return false;
        CommitText();
        var action = HitTestMosaicHandle(screenPoint, scale);
        if (action == SelectionAction.None || SelectedMosaicIndex is not { } index) return false;
        BeginMosaicEdit(index, action, screenPoint);
        return true;
    }

    private void BeginMosaicEdit(int index, SelectionAction action, PixelPoint screenPoint)
    {
        SelectedMosaicIndex = index;
        _movingMosaic = Document.Items[index];
        MosaicMovePreview = _movingMosaic;
        _mosaicMoveStart = screenPoint;
        _mosaicResizeBounds = PixelRect.Between(_movingMosaic.Start, _movingMosaic.End)
            .Intersect(new(0, 0, Selection.Selection.Width, Selection.Selection.Height));
        MosaicAction = action;
        Status = "";
        Refresh();
    }

    public void UpdateMosaicMove(PixelPoint screenPoint)
    {
        if (_movingMosaic is not { } original) return;
        if (MosaicAction != SelectionAction.Move)
        {
            UpdateMosaicResize(original, screenPoint);
            Refresh();
            return;
        }
        var bounds = PixelRect.Between(original.Start, original.End);
        var selection = Selection.Selection;
        // A previously resized capture may clip a larger annotation; keep its size and allow its visible part to move.
        var x1 = -bounds.X; var x2 = selection.Width - bounds.Right;
        var y1 = -bounds.Y; var y2 = selection.Height - bounds.Bottom;
        var dx = ClampMosaicOffset(screenPoint.X - _mosaicMoveStart.X, x1, x2);
        var dy = ClampMosaicOffset(screenPoint.Y - _mosaicMoveStart.Y, y1, y2);
        MosaicMovePreview = original with { Start = new(original.Start.X + dx, original.Start.Y + dy),
            End = new(original.End.X + dx, original.End.Y + dy) };
        Refresh();
    }

    private void UpdateMosaicResize(ScreenshotAnnotation original, PixelPoint screenPoint)
    {
        var bounds = _mosaicResizeBounds;
        var selection = Selection.Selection;
        var dx = screenPoint.X - _mosaicMoveStart.X;
        var dy = screenPoint.Y - _mosaicMoveStart.Y;
        var left = bounds.X; var right = bounds.Right;
        var top = bounds.Y; var bottom = bounds.Bottom;
        if (MosaicAction.HasFlag(SelectionAction.Left)) left = Math.Clamp(left + dx, 0, right - 1);
        if (MosaicAction.HasFlag(SelectionAction.Right)) right = Math.Clamp(right + dx, left + 1, selection.Width);
        if (MosaicAction.HasFlag(SelectionAction.Top)) top = Math.Clamp(top + dy, 0, bottom - 1);
        if (MosaicAction.HasFlag(SelectionAction.Bottom)) bottom = Math.Clamp(bottom + dy, top + 1, selection.Height);
        MosaicMovePreview = original with
        {
            Start = new(original.Start.X > original.End.X ? right : left, original.Start.Y > original.End.Y ? bottom : top),
            End = new(original.Start.X > original.End.X ? left : right, original.Start.Y > original.End.Y ? top : bottom)
        };
    }

    public void EndMosaicMove()
    {
        var index = SelectedMosaicIndex;
        var original = _movingMosaic;
        var preview = MosaicMovePreview;
        _movingMosaic = null;
        MosaicMovePreview = null;
        MosaicAction = SelectionAction.None;
        if (index is { } i && original is not null && preview is not null && i < Document.Items.Count
            && Document.Items[i] == original && Document.ReplaceAt(i, preview)) SelectedMosaicIndex = i;
        Refresh();
    }

    public void CancelMosaicMove() { ClearMosaicEdit(); Refresh(); }
    private void ClearMosaicEdit() { _movingMosaic = null; MosaicMovePreview = null; MosaicAction = SelectionAction.None; }
    private static int ClampMosaicOffset(int offset, int minimum, int maximum) => minimum <= maximum
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
        CancelMosaicMove();
        SelectedMosaicIndex = null;
        if (Selection.HitTest(point, tolerance) == SelectionAction.New) Document.Reset();
        Selection.Begin(point, tolerance);
    }

    public void BeginStroke(PixelPoint screenPoint, double scale)
    {
        if (IsBusy || Selection.Selection.IsEmpty || !Selection.Selection.Contains(screenPoint)) return;
        CommitText();
        CancelMosaicMove();
        SelectedMosaicIndex = null;
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
        if (draft is not null && Document.Add(draft) && draft.Tool == CaptureTool.Mosaic)
            SelectedMosaicIndex = Document.Items.Count - 1;
        Refresh();
    }
    public void CancelStroke() { _draft = null; _points.Clear(); Refresh(); }
    private static bool UsesPath(ScreenshotAnnotation annotation) => annotation.Tool == CaptureTool.Pen
        || annotation.Tool == CaptureTool.Arrow && annotation.ArrowStyle == ArrowStyle.Polyline;
    private void DocumentChanged()
    {
        ClearMosaicEdit();
        SelectedMosaicIndex = null;
        Refresh();
    }
    private void SelectionChanged()
    {
        if (_lastSelection != Selection.Selection)
        {
            _lastSelection = Selection.Selection;
            ClearMosaicEdit();
            SelectedMosaicIndex = null;
        }
        Refresh();
    }
    private void Refresh() => Changed?.Invoke();
    public void Dispose() { Selection.Changed -= SelectionChanged; Document.Changed -= DocumentChanged; }
}
