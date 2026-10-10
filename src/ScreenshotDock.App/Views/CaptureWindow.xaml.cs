using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App.Capture;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

public partial class CaptureWindow : Window
{
    private readonly MonitorDescriptor _monitor;
    private readonly IReadOnlyList<MonitorDescriptor> _monitors;
    private readonly CaptureEditSession _editor;
    private readonly SelectionCanvas _canvas;
    private readonly bool _ownsEditor;
    private readonly Dictionary<CaptureTool, Button> _toolButtons;
    private readonly Dictionary<ArrowStyle, Button> _arrowStyleButtons;
    private PixelPoint? _textAnchor;
    private const double TextEditorInset = 6;
    private double _textMaxWidth;
    private bool _syncingStyle;
    public event Action? CompleteRequested;
    public event Action? CancelRequested;
    public event Action? CopyRequested;
    public event Action? SaveRequested;
    public event Action? PinRequested;
    public CaptureEditSession Editor => _editor;
    public CaptureWindow(DesktopFrame frame, MonitorDescriptor monitor, SelectionSession session, CaptureEditSession? editor = null,
        SmartSelectionSession? smartSelection = null)
    {
        _monitor = monitor;
        _monitors = DesktopEnvironment.Monitors;
        _ownsEditor = editor is null;
        _editor = editor ?? new(session);
        InitializeComponent();
        Cursor = CaptureCursors.Crosshair;
        AnnotationTextBox.Cursor = CaptureCursors.Text;
        _toolButtons = new()
        {
            [CaptureTool.Select] = SelectToolButton, [CaptureTool.Rectangle] = RectangleToolButton,
            [CaptureTool.Ellipse] = EllipseToolButton, [CaptureTool.Arrow] = ArrowToolButton,
            [CaptureTool.Pen] = PenToolButton, [CaptureTool.Mosaic] = MosaicToolButton,
            [CaptureTool.Text] = TextToolButton, [CaptureTool.Eraser] = EraserToolButton
        };
        _arrowStyleButtons = new()
        {
            [ArrowStyle.Line] = ArrowLineStyleButton, [ArrowStyle.Solid] = ArrowSolidStyleButton,
            [ArrowStyle.Tapered] = ArrowTaperedStyleButton, [ArrowStyle.Outline] = ArrowOutlineStyleButton,
            [ArrowStyle.Polyline] = ArrowPolylineStyleButton
        };
        _canvas = new SelectionCanvas(frame, monitor, _editor, smartSelection);
        Surface.Children.Insert(0, _canvas);
        _canvas.CompleteRequested += RequestCompletion;
        _editor.Changed += RefreshEditor;
        _editor.CommitTextRequested += CommitText;
        _editor.TextRequested += BeginText;
        SourceInitialized += (_, _) => DesktopEnvironment.Place(this, _monitor.Bounds);
        Loaded += (_, _) =>
        {
            DesktopEnvironment.Place(this, _monitor.Bounds); _canvas.Focus(); RefreshEditor();
            if (_monitor.Bounds.Contains(DesktopEnvironment.Cursor)) smartSelection?.Hover(DesktopEnvironment.Cursor);
        };
        Activated += (_, _) => { if (AnnotationTextBox.Visibility != Visibility.Visible) _canvas.Focus(); };
        SizeChanged += (_, _) => RefreshEditor();
        Closed += (_, _) =>
        {
            _canvas.Detach();
            _editor.Changed -= RefreshEditor;
            _editor.CommitTextRequested -= CommitText;
            _editor.TextRequested -= BeginText;
            if (_ownsEditor) _editor.Dispose();
        };
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right) e.Handled = true;
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            e.Handled = true;
            // Finish the right click here so its release cannot open a menu on the desktop underneath.
            if (!_editor.IsBusy) CancelRequested?.Invoke();
        }
        base.OnPreviewMouseUp(e);
    }

    private void CaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor.IsBusy || AnnotationTextBox.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Escape)
        {
            if (_editor.IsMovingMosaic) { _editor.CancelMosaicMove(); _canvas.ReleaseMouseCapture(); }
            else if (_editor.IsDrawing) { _editor.CancelStroke(); _canvas.ReleaseMouseCapture(); }
            else CancelRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { RequestCompletion(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z) UndoClick(sender, e);
            else if (e.Key == Key.Y) RedoClick(sender, e);
            else if (e.Key == Key.S) SaveClick(sender, e);
            else if (e.Key == Key.C) CopyClick(sender, e);
            else return;
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            var tool = e.Key switch { Key.V => CaptureTool.Select, Key.R => CaptureTool.Rectangle, Key.O => CaptureTool.Ellipse,
                Key.A => CaptureTool.Arrow, Key.B => CaptureTool.Pen, Key.M => CaptureTool.Mosaic,
                Key.T => CaptureTool.Text, Key.E => CaptureTool.Eraser, _ => (CaptureTool?)null };
            if (tool.HasValue) { _editor.SetTool(tool.Value); e.Handled = true; }
        }
    }

    private void RefreshEditor()
    {
        if (_toolButtons is null) return; // Initial XAML selection events run before construction completes.
        foreach (var pair in _toolButtons) pair.Value.Background = pair.Key == _editor.Tool ? new SolidColorBrush(Color.FromRgb(213, 233, 255)) : Brushes.Transparent;
        foreach (var pair in _arrowStyleButtons) pair.Value.Background = pair.Key == _editor.ArrowStyle ? new SolidColorBrush(Color.FromRgb(213, 233, 255)) : Brushes.Transparent;
        UndoButton.IsEnabled = _editor.Document.CanUndo;
        RedoButton.IsEnabled = _editor.Document.CanRedo;
        CaptureToolbar.IsEnabled = !_editor.IsBusy;
        _canvas.IsEnabled = !_editor.IsBusy;
        AnnotationOptions.Visibility = _editor.Tool is CaptureTool.Select or CaptureTool.Eraser or CaptureTool.Mosaic ? Visibility.Collapsed : Visibility.Visible;
        MosaicOptions.Visibility = _editor.Tool == CaptureTool.Mosaic ? Visibility.Visible : Visibility.Collapsed;
        FontSizeOptions.Visibility = _editor.Tool == CaptureTool.Text ? Visibility.Visible : Visibility.Collapsed;
        ArrowStyleOptions.Visibility = _editor.Tool == CaptureTool.Arrow ? Visibility.Visible : Visibility.Collapsed;
        CaptureStatus.Text = _editor.Status;
        CaptureStatus.Visibility = string.IsNullOrEmpty(_editor.Status) ? Visibility.Collapsed : Visibility.Visible;
        if (_textAnchor.HasValue)
        {
            AnnotationTextBox.Foreground = ColorBrush();
            AnnotationTextBox.FontSize = _editor.FontSize;
            UpdateTextEditorSize();
        }
        var selection = _editor.Selection.Selection;
        var owner = _monitors.FirstOrDefault(m => m.Bounds.Contains(new(selection.Right - 1, selection.Bottom - 1)))
            ?? _monitors.OrderByDescending(m => (long)m.Bounds.Intersect(selection).Width * m.Bounds.Intersect(selection).Height).First();
        var visible = selection.Width >= 2 && selection.Height >= 2 && _editor.Selection.Action == SelectionAction.None
            && !_editor.IsDrawing && !_editor.IsMovingMosaic && owner.Handle == _monitor.Handle;
        CaptureToolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || ActualWidth <= 0 || ActualHeight <= 0) return;
        CaptureToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = CaptureToolbar.DesiredSize.Width;
        var height = CaptureToolbar.DesiredSize.Height;
        var bottomRight = PointFromScreen(new Point(selection.Right, selection.Bottom));
        var topLeft = PointFromScreen(new Point(selection.X, selection.Y));
        var y = bottomRight.Y + 12;
        if (y + height > ActualHeight - 8)
            y = topLeft.Y - height - 12 >= 8 ? topLeft.Y - height - 12 : bottomRight.Y - height - 12;
        Canvas.SetLeft(CaptureToolbar, Math.Clamp(bottomRight.X - width, 8, Math.Max(8, ActualWidth - width - 8)));
        Canvas.SetTop(CaptureToolbar, Math.Clamp(y, 8, Math.Max(8, ActualHeight - height - 8)));
    }

    private Brush ColorBrush() => new SolidColorBrush(Color.FromArgb((byte)(_editor.ColorArgb >> 24), (byte)(_editor.ColorArgb >> 16), (byte)(_editor.ColorArgb >> 8), (byte)_editor.ColorArgb));
    private void ToolClick(object sender, RoutedEventArgs e) { if (sender is Button { Tag: CaptureTool tool }) _editor.SetTool(tool); _canvas.Focus(); }
    private void ArrowStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ArrowStyle style }) _editor.SetArrowStyle(style);
        _canvas.Focus();
    }
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        var value = (Color)ColorConverter.ConvertFromString(color);
        _editor.ColorArgb = (uint)(value.A << 24 | value.R << 16 | value.G << 8 | value.B);
        _editor.NotifyStyleChanged();
    }
    private void StyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingStyle) return;
        if (ReferenceEquals(sender, StrokeWidthBox) && StrokeWidthBox.SelectedItem is ComboBoxItem { Content: string width }) _editor.Thickness = double.Parse(width, CultureInfo.InvariantCulture);
        if (ReferenceEquals(sender, FontSizeBox) && FontSizeBox.SelectedItem is ComboBoxItem { Content: string size }) _editor.FontSize = double.Parse(size, CultureInfo.InvariantCulture);
        if (ReferenceEquals(sender, MosaicSizeBox) && MosaicSizeBox.SelectedItem is ComboBoxItem { Content: string mosaic }) _editor.MosaicSize = double.Parse(mosaic, CultureInfo.InvariantCulture);
        _editor.NotifyStyleChanged();
    }
    private void StylePickerClosed(object sender, EventArgs e)
    {
        if (AnnotationTextBox.Visibility == Visibility.Visible) AnnotationTextBox.Focus();
        else _canvas.Focus();
    }
    private void UndoClick(object sender, RoutedEventArgs e) { _editor.CommitText(); _editor.CancelStroke(); _editor.CancelMosaicMove(); _editor.Document.Undo(); }
    private void RedoClick(object sender, RoutedEventArgs e) { _editor.CommitText(); _editor.CancelStroke(); _editor.CancelMosaicMove(); _editor.Document.Redo(); }
    private void CompleteClick(object sender, RoutedEventArgs e) => RequestCompletion();
    private void RequestCompletion() { PrepareCompletion(); if (ValidSelection()) CompleteRequested?.Invoke(); }
    private bool ValidSelection() => !_editor.IsBusy && _editor.Selection.Selection.Width >= 2 && _editor.Selection.Selection.Height >= 2;
    private void PrepareCompletion() { _editor.CommitText(); if (_editor.IsDrawing) _editor.EndStroke(); _editor.EndMosaicMove(); }
    private void CopyClick(object sender, RoutedEventArgs e) { PrepareCompletion(); if (ValidSelection()) CopyRequested?.Invoke(); }
    private void SaveClick(object sender, RoutedEventArgs e) { PrepareCompletion(); if (ValidSelection()) SaveRequested?.Invoke(); }
    private void PinClick(object sender, RoutedEventArgs e) => RequestPin();
    public void RequestPin() { PrepareCompletion(); if (ValidSelection()) PinRequested?.Invoke(); }
    public void SetPinShortcut(HotkeySettings shortcut) => PinCaptureButton.ToolTip = $"贴到屏幕 ({shortcut.Display}) · 点击选中，Esc 移除";
    private void CancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private void BeginText(PixelPoint screenPoint)
    {
        if (!_monitor.Bounds.Contains(screenPoint)) return;
        var selection = _editor.Selection.Selection;
        var intersection = selection.Intersect(_monitor.Bounds);
        var scale = _canvas.PixelsPerDip;
        var original = _editor.EditingText;
        var inset = (int)Math.Round(TextEditorInset * scale);
        var x = original is null ? Math.Clamp(screenPoint.X, intersection.X, Math.Max(intersection.X, intersection.Right - (int)(50 * scale)))
            : selection.X + original.Start.X - inset;
        var y = original is null ? Math.Clamp(screenPoint.Y, intersection.Y, Math.Max(intersection.Y, intersection.Bottom - (int)(38 * scale)))
            : selection.Y + original.Start.Y - inset;
        var point = PointFromScreen(new Point(x, y));
        _textAnchor = original?.Start ?? new(x - selection.X + inset, y - selection.Y + inset);
        _textMaxWidth = Math.Max(1, (intersection.Right - x) / scale);
        AnnotationTextBox.MaxHeight = Math.Max(1, (intersection.Bottom - y) / scale);
        AnnotationTextBox.MinHeight = Math.Min(38, AnnotationTextBox.MaxHeight);
        if (original is not null)
        {
            _editor.ColorArgb = original.ColorArgb;
            _editor.FontSize = original.FontSize / scale;
            _syncingStyle = true;
            FontSizeBox.SelectedItem = FontSizeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Content is string value && double.Parse(value, CultureInfo.InvariantCulture) == _editor.FontSize);
            _syncingStyle = false;
        }
        AnnotationTextBox.Text = original?.Text ?? "";
        AnnotationTextBox.FontSize = _editor.FontSize;
        AnnotationTextBox.Foreground = ColorBrush();
        Canvas.SetLeft(AnnotationTextBox, point.X);
        Canvas.SetTop(AnnotationTextBox, point.Y);
        AnnotationTextBox.Visibility = Visibility.Visible;
        UpdateTextEditorSize();
        Activate();
        AnnotationTextBox.Focus();
        AnnotationTextBox.CaretIndex = AnnotationTextBox.Text.Length;
    }

    private void TextEditorChanged(object sender, TextChangedEventArgs e) => UpdateTextEditorSize();
    private void UpdateTextEditorSize()
    {
        if (!_textAnchor.HasValue || _textMaxWidth <= 0) return;
        var content = new FormattedText(string.IsNullOrEmpty(AnnotationTextBox.Text) ? " " : AnnotationTextBox.Text,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(AnnotationTextBox.FontFamily, AnnotationTextBox.FontStyle, AnnotationTextBox.FontWeight, AnnotationTextBox.FontStretch),
            AnnotationTextBox.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(AnnotationTextBox).PixelsPerDip);
        // Reserve one character for the caret and ongoing IME composition before wrapping at the actual selection edge.
        var width = content.WidthIncludingTrailingWhitespace + TextEditorInset * 2 + AnnotationTextBox.FontSize;
        AnnotationTextBox.Width = Math.Clamp(width, Math.Min(40, _textMaxWidth), _textMaxWidth);
    }

    public void CommitText()
    {
        if (!_textAnchor.HasValue) return;
        var anchor = _textAnchor.Value;
        var text = AnnotationTextBox.Text;
        _textAnchor = null;
        AnnotationTextBox.Visibility = Visibility.Collapsed;
        var scale = _canvas.PixelsPerDip;
        var annotation = new ScreenshotAnnotation(CaptureTool.Text, anchor, anchor, _editor.ColorArgb, _editor.Thickness * scale,
            _editor.FontSize * scale, text, TextWidth: Math.Max(1, (AnnotationTextBox.Width - TextEditorInset * 2) * scale));
        if (_editor.EditingText is { } original && original.Text == annotation.Text
            && original.ColorArgb == annotation.ColorArgb && Math.Abs(original.FontSize - annotation.FontSize) < 0.01)
            annotation = original;
        _editor.SaveText(annotation);
        _canvas.Focus();
    }

    private void TextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CommitText(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            _textAnchor = null;
            AnnotationTextBox.Visibility = Visibility.Collapsed;
            _editor.CancelTextEdit();
            _canvas.Focus();
            e.Handled = true;
        }
    }
}

internal sealed class SelectionCanvas : FrameworkElement
{
    private readonly DesktopFrame _frame;
    private readonly MonitorDescriptor _monitor;
    private readonly SelectionSession _session;
    private readonly CaptureEditSession _editor;
    private readonly SmartSelectionSession? _smartSelection;
    private readonly BitmapSource _background;
    private BitmapSource? _mosaicSource;
    private PixelRect _mosaicSelection;
    public event Action? CompleteRequested;

    public double PixelsPerDip => ScaleX;
    public SelectionCanvas(DesktopFrame frame, MonitorDescriptor monitor, CaptureEditSession editor, SmartSelectionSession? smartSelection)
    {
        _monitor = monitor;
        _frame = frame;
        _editor = editor;
        _smartSelection = smartSelection;
        _session = editor.Selection;
        _background = ScreenCaptureService.Crop(frame, monitor.Bounds);
        _editor.Changed += Refresh;
        if (_smartSelection is not null) _smartSelection.Changed += InvalidateVisual;
        Focusable = true;
        Cursor = CaptureCursors.Crosshair;
    }

    public void Detach()
    {
        _editor.Changed -= Refresh;
        if (_smartSelection is not null) _smartSelection.Changed -= InvalidateVisual;
    }
    private void Refresh() { InvalidateVisual(); UpdateCursor(DesktopEnvironment.Cursor); }
    private Point Local(int x, int y) => new((x - _monitor.Bounds.X) / ScaleX, (y - _monitor.Bounds.Y) / ScaleY);
    private double ScaleX => _monitor.Bounds.Width / Math.Max(1, ActualWidth);
    private double ScaleY => _monitor.Bounds.Height / Math.Max(1, ActualHeight);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.DrawImage(_background, full);
        var isPreview = _session.Selection.IsEmpty && _smartSelection is { Preview.IsEmpty: false };
        var selection = isPreview ? _smartSelection!.Preview : _session.Selection;
        var p1 = Local(selection.X, selection.Y);
        var p2 = Local(selection.Right, selection.Bottom);
        var rect = new Rect(p1, p2);
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(full));
        if (!selection.IsEmpty) shade.Children.Add(new RectangleGeometry(rect));
        drawing.PushClip(new RectangleGeometry(full));
        drawing.DrawGeometry((Brush)FindResource("CaptureMask"), null, shade);
        if (!selection.IsEmpty)
        {
            drawing.PushClip(new RectangleGeometry(rect));
            drawing.PushTransform(new TranslateTransform(rect.Left, rect.Top));
            drawing.PushTransform(new ScaleTransform(1 / ScaleX, 1 / ScaleY));
            BitmapSource? source = null;
            if (_editor.Document.Items.Any(a => a.Tool == CaptureTool.Mosaic) || _editor.Draft?.Tool == CaptureTool.Mosaic)
            {
                if (_mosaicSource is null || _mosaicSelection != selection)
                {
                    _mosaicSource = ScreenCaptureService.Crop(_frame, selection);
                    _mosaicSelection = selection;
                }
                source = _mosaicSource;
            }
            for (var i = 0; i < _editor.Document.Items.Count; i++)
                if (i != _editor.EditingTextIndex) AnnotationRenderer.Draw(drawing,
                    i == _editor.SelectedMosaicIndex && _editor.MosaicMovePreview is { } preview ? preview : _editor.Document.Items[i], source);
            if (_editor.Draft is { } draft) AnnotationRenderer.Draw(drawing, draft, source);
            drawing.Pop(); drawing.Pop();
            var activeMosaic = _editor.Draft?.Tool == CaptureTool.Mosaic ? _editor.Draft : _editor.SelectedMosaic;
            if (activeMosaic is not null)
            {
                var bounds = PixelRect.Between(activeMosaic.Start, activeMosaic.End);
                var outline = new Rect(Local(selection.X + bounds.X, selection.Y + bounds.Y),
                    Local(selection.X + bounds.Right, selection.Y + bounds.Bottom));
                // Keep the one-DIP white stroke inside the region and crisp on whole-pixel bounds.
                if (outline.Width >= 1 && outline.Height >= 1) outline.Inflate(-0.5, -0.5);
                // Matching dash lengths in DIPs keep the dark underlay aligned with the white editing outline.
                var underlay = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.5) { DashStyle = new DashStyle([2, 8.0 / 3], 0) };
                var white = new Pen(Brushes.White, 1) { DashStyle = new DashStyle([3, 4], 0) };
                drawing.DrawRectangle(null, underlay, outline);
                drawing.DrawRectangle(null, white, outline);
                if (!_editor.IsDrawing)
                {
                    var fill = new SolidColorBrush(Color.FromRgb(35, 37, 41));
                    var border = new Pen(Brushes.White, 1);
                    foreach (var (point, _) in MosaicSelectionGeometry.Handles(bounds))
                    {
                        var center = Local(selection.X + point.X, selection.Y + point.Y);
                        drawing.DrawRectangle(fill, border, new Rect(center.X - 2.5, center.Y - 2.5, 6, 6));
                    }
                }
            }
            drawing.Pop();
            var selectionBrush = (Brush)FindResource("SelectionOutline");
            drawing.DrawRectangle(null, new Pen(selectionBrush, 3), rect);
            if (!isPreview) foreach (var point in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight,
                new Point(rect.Left + rect.Width / 2, rect.Top), new Point(rect.Left + rect.Width / 2, rect.Bottom),
                new Point(rect.Left, rect.Top + rect.Height / 2), new Point(rect.Right, rect.Top + rect.Height / 2) })
                drawing.DrawRectangle(Brushes.White, new Pen(selectionBrush, 2), new Rect(point.X - 4, point.Y - 4, 8, 8));
            if (_monitor.Bounds.Contains(new(selection.X, selection.Y)))
                DrawLabel(drawing, $"{selection.Width} × {selection.Height} px" + (isPreview ? " · 自动识别" : ""),
                    Math.Clamp(rect.Left, 12, Math.Max(12, ActualWidth - 230)),
                    rect.Top > 48 ? rect.Top - 37 : Math.Min(ActualHeight - 42, rect.Bottom + 12), isPreview ? 220 : 175, 30);
        }
        var hint = isPreview ? "单击选中识别区域 · 拖动自由框选    |    Esc / 右键取消"
            : selection.IsEmpty ? "移动鼠标自动识别 · 拖动自由框选    |    Esc / 右键取消" : _editor.Tool == CaptureTool.Mosaic
            ? "框选马赛克 · 拖动区域移动 · 控制点调整大小    |    Ctrl+Z 撤销"
            : _editor.Tool == CaptureTool.Select
            ? "调整选区 · 右下角工具栏标注    |    Enter 完成 · Esc / 右键取消"
            : "在选区内标注 · Ctrl+Z 撤销    |    Enter 完成 · Esc / 右键取消";
        DrawLabel(drawing, hint, Math.Max(10, (ActualWidth - 550) / 2), ActualHeight - 74, Math.Min(550, ActualWidth - 20), 38);
        drawing.Pop();
    }

    private void DrawLabel(DrawingContext drawing, string value, double x, double y, double width, double height)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawRoundedRectangle((Brush)FindResource("CaptureLabel"), null, new Rect(x, y, width, height), 7, 7);
        drawing.DrawText(text, new Point(x + (width - text.Width) / 2, y + (height - text.Height) / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_editor.IsBusy) { e.Handled = true; return; }
        var point = ScreenPoint(e);
        if (_smartSelection?.Begin(point, Math.Max(SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance) * ScaleX) == true)
        {
            Focus(); CaptureMouse(); e.Handled = true; return;
        }
        if (_editor.Tool == CaptureTool.Select && _editor.TryEditText(point, ScaleX)) { e.Handled = true; return; }
        if ((_editor.Tool != CaptureTool.Select || _session.HitTest(point, (int)(7 * ScaleX)) == SelectionAction.Move)
            && (_editor.TryBeginMosaicResize(point, ScaleX) || _editor.TryBeginMosaicMove(point, ScaleX)))
        {
            Focus(); CaptureMouse(); e.Handled = true; return;
        }
        if (e.ClickCount == 2 && _editor.Tool == CaptureTool.Select && _session.Selection.Contains(point)) { CompleteRequested?.Invoke(); e.Handled = true; return; }
        Focus();
        if (_editor.Tool == CaptureTool.Select || _session.Selection.IsEmpty)
        {
            _editor.BeginSelection(point, (int)(7 * ScaleX));
            CaptureMouse();
        }
        else
        {
            _editor.BeginStroke(point, ScaleX);
            if (_editor.IsDrawing) CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = ScreenPoint(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_smartSelection?.IsSelecting == true) _smartSelection.Update(point);
            else if (_editor.IsMovingMosaic) _editor.UpdateMosaicMove(point);
            else if (_editor.IsDrawing) _editor.UpdateStroke(point);
            else _session.Update(point);
        }
        else if (e.LeftButton == MouseButtonState.Released) _smartSelection?.Hover(point);
        UpdateCursor(point);
    }

    private void UpdateCursor(PixelPoint point)
    {
        if (_session.Selection.IsEmpty) { Cursor = CaptureCursors.Crosshair; return; }
        if (_editor.IsMovingMosaic) { Cursor = ActionCursor(_editor.MosaicAction); return; }
        if (_editor.Tool == CaptureTool.Mosaic || _session.HitTest(point, (int)(7 * ScaleX)) == SelectionAction.Move)
        {
            var handle = _editor.HitTestMosaicHandle(point, ScaleX);
            if (handle != SelectionAction.None) { Cursor = ActionCursor(handle); return; }
            if (_editor.HitTestMosaic(point, ScaleX).HasValue) { Cursor = CaptureCursors.Move; return; }
        }
        if (_editor.Tool != CaptureTool.Select) { Cursor = _editor.Tool == CaptureTool.Text ? CaptureCursors.Text : CaptureCursors.Crosshair; return; }
        var action = _session.HitTest(point, (int)(7 * ScaleX));
        Cursor = ActionCursor(action);
    }

    private static Cursor ActionCursor(SelectionAction action) => action switch
        {
            SelectionAction.Move => CaptureCursors.Move,
            SelectionAction.Left or SelectionAction.Right => CaptureCursors.Horizontal,
            SelectionAction.Top or SelectionAction.Bottom => CaptureCursors.Vertical,
            SelectionAction.Left | SelectionAction.Top or SelectionAction.Right | SelectionAction.Bottom => CaptureCursors.DiagonalDown,
            SelectionAction.Right | SelectionAction.Top or SelectionAction.Left | SelectionAction.Bottom => CaptureCursors.DiagonalUp,
            _ => CaptureCursors.Crosshair
        };

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            if (_smartSelection?.IsSelecting == true) _smartSelection.End(ScreenPoint(e));
            else if (_editor.IsMovingMosaic) { _editor.UpdateMosaicMove(ScreenPoint(e)); _editor.EndMosaicMove(); }
            else if (_editor.IsDrawing) { _editor.UpdateStroke(ScreenPoint(e)); _editor.EndStroke(); }
            else { _session.Update(ScreenPoint(e)); _session.End(); }
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    private PixelPoint ScreenPoint(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return new(_monitor.Bounds.X + (int)Math.Round(point.X * ScaleX), _monitor.Bounds.Y + (int)Math.Round(point.Y * ScaleY));
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_smartSelection?.IsSelecting == true) _smartSelection.CancelPointer();
        if (_editor.IsMovingMosaic) _editor.CancelMosaicMove();
        if (_editor.IsDrawing) _editor.CancelStroke();
        if (_session.Action != SelectionAction.None) _session.End();
    }
}
