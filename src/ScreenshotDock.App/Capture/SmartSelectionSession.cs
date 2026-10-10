using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.App.Capture;

public sealed class SmartSelectionSession : IDisposable
{
    private readonly CaptureEditSession _editor;
    private readonly ICaptureRegionDetector _detector;
    private CancellationTokenSource? _hover;
    private PixelPoint? _lastPoint;
    private PixelPoint _start;
    private PixelRect _candidate;
    private double _dragThreshold;
    private bool _dragging;
    private bool _disposed;
    private int _version;
    public PixelRect Preview { get; private set; }
    public bool IsSelecting { get; private set; }
    public event Action? Changed;

    public SmartSelectionSession(CaptureEditSession editor, ICaptureRegionDetector detector)
    {
        _editor = editor;
        _detector = detector;
        editor.Selection.Changed += SelectionChanged;
    }

    public void Hover(PixelPoint point)
    {
        if (_disposed || IsSelecting || !_editor.Selection.Selection.IsEmpty || _editor.Selection.Action != SelectionAction.None) return;
        if (_lastPoint == point) return;
        _lastPoint = point;
        CancelHover();
        var fallback = _detector.HitTest(point).Intersect(_editor.Selection.Bounds);
        if (!Preview.Contains(point) || Preview.Intersect(fallback) != Preview) SetPreview(fallback);
        _hover = new CancellationTokenSource();
        _ = RefineAsync(point, _version, _hover.Token);
    }

    private async Task RefineAsync(PixelPoint point, int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token);
            var refined = await _detector.FindControlAsync(point, token);
            if (_disposed || token.IsCancellationRequested || version != _version || IsSelecting
                || !_editor.Selection.Selection.IsEmpty || _editor.Selection.Action != SelectionAction.None) return;
            if (refined is { } region)
            {
                region = region.Intersect(_editor.Selection.Bounds);
                if (region.Width >= 2 && region.Height >= 2 && region.Contains(point)) SetPreview(region);
            }
        }
        catch (OperationCanceledException) { }
    }

    public bool Begin(PixelPoint point, double dragThreshold)
    {
        if (_disposed || !_editor.Selection.Selection.IsEmpty || _editor.Selection.Action != SelectionAction.None) return false;
        CancelHover();
        _candidate = Preview.Contains(point) ? Preview : _detector.HitTest(point).Intersect(_editor.Selection.Bounds);
        _start = point;
        _dragThreshold = Math.Max(1, dragThreshold);
        IsSelecting = true;
        _dragging = false;
        return true;
    }

    public void Update(PixelPoint point)
    {
        if (!IsSelecting) return;
        if (!_dragging && (Math.Abs(point.X - _start.X) >= _dragThreshold || Math.Abs(point.Y - _start.Y) >= _dragThreshold))
        {
            _dragging = true;
            SetPreview(default);
            _editor.BeginSelection(_start, 0);
        }
        if (_dragging) _editor.Selection.Update(point);
    }

    public void End(PixelPoint point)
    {
        if (!IsSelecting) return;
        Update(point);
        IsSelecting = false;
        SetPreview(default);
        if (_dragging) _editor.Selection.End();
        else if (_candidate.Width >= 2 && _candidate.Height >= 2) _editor.Selection.SetSelection(_candidate);
        _lastPoint = null;
    }

    public void CancelPointer()
    {
        IsSelecting = false;
        _lastPoint = null;
        SetPreview(default);
    }

    private void SelectionChanged()
    {
        if (!_editor.Selection.Selection.IsEmpty || _editor.Selection.Action != SelectionAction.None)
        { CancelHover(); SetPreview(default); }
        else _lastPoint = null;
    }
    private void SetPreview(PixelRect region) { if (Preview == region) return; Preview = region; Changed?.Invoke(); }
    private void CancelHover() { _version++; _hover?.Cancel(); _hover?.Dispose(); _hover = null; }
    public void Dispose()
    {
        _disposed = true;
        CancelHover();
        _editor.Selection.Changed -= SelectionChanged;
        CancelPointer();
    }
}
