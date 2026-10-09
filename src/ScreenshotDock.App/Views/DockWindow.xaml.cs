using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenshotDock.App.ViewModels;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Clipboard;
using ScreenshotDock.Windows.Input;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

public partial class DockWindow : Window
{
    private readonly DockBackdropWindow _backdrop = new();
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _copyTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ScreenshotCard[] _cards = [];
    private Point _dragStart;
    private ScreenshotCard? _pressed;
    private bool _didDrag;
    private bool _menuOpen;
    private DateTime _keepOpenUntil;
    private DateTime? _outsideSince;
    private bool _hasEnteredHotArea;
    private TaskCompletionSource<bool>? _collapseCompletion;
    private bool _cancelCollapseOnPointerReturn;
    private HotAreaWheelListener? _wheelListener;
    private double? _pendingWheelOffset;
    public bool IsLocked { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsConfirmationOpen { get; private set; }
    public bool IsBusy => IsDragging || _menuOpen || IsConfirmationOpen;
    public event Action? CaptureRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? CollapseRequested;
    public event Action<bool>? LockChanged;
    public event Action<ScreenshotCard>? CopyRequested;
    public event Action<ScreenshotCard>? PreviewRequested;
    public event Action<ScreenshotCard>? SaveRequested;
    public event Action<ScreenshotCard>? DeleteRequested;
    public event Action? ClearRequested;
    public event Action<Exception>? Error;

    public DockWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            DesktopEnvironment.DoNotActivate(this);
            DesktopEnvironment.ExcludeFromCapture(this);
            _wheelListener = new(new System.Windows.Interop.WindowInteropHelper(this).Handle, () => IsVisible && !IsBusy);
            _wheelListener.Wheel += ScrollCards;
            if (IsVisible) StartWheelCapture();
        };
        _hideTimer.Tick += (_, _) => CheckHotArea();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusLabel.Text = "单击复制 · 拖拽分享 · 右键更多操作"; UpdateHintVisibility(); };
        _copyTimer.Tick += (_, _) => { _copyTimer.Stop(); foreach (var card in _cards) card.IsCopied = false; };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _backdrop.Show();
                PositionAtTop();
                StartWheelCapture();
                return;
            }
            _backdrop.Hide();
            _wheelListener?.Stop();
            _pendingWheelOffset = null;
            _hideTimer.Stop();
            _outsideSince = null;
            _hasEnteredHotArea = false;
            ResetTransitionAnimations();
        };
        Closed += (_, _) => { _hideTimer.Stop(); _statusTimer.Stop(); _copyTimer.Stop(); _wheelListener?.Dispose(); ResetTransitionAnimations(); _backdrop.Close(); };
    }

    public void SetCards(IEnumerable<ScreenshotCard> cards)
    {
        var items = cards.ToArray();
        double[] angles = [-2.8, 1.2, -3.2, 2.4];
        for (var i = 0; i < items.Length; i++) items[i].Tilt = angles[i % angles.Length];
        _cards = items;
        _pendingWheelOffset = null;
        Cards.ItemsSource = items;
        CountLabel.Text = $"{items.Length} 张截图";
        UpdateHintVisibility();
        ClearButton.IsEnabled = items.Length > 0;
    }

    public void Configure(AppSettings settings)
    {
        EmptyHint.Text = $"按 {settings.Hotkey.Display} 框选屏幕，截图会出现在这里";
        CaptureButton.ToolTip = settings.Hotkey.Display;
        SetLocked(settings.DockLocked);
    }

    public void SetLocked(bool locked)
    {
        IsLocked = locked;
        LockButton.Background = locked ? (Brush)FindResource("Soft") : Brushes.Transparent;
        LockButton.ToolTip = locked ? "取消锁定，恢复自动收起" : "锁定展开";
        if (locked)
        {
            _hideTimer.Stop();
            if (_collapseCompletion is not null) Expand(TimeSpan.Zero);
        }
        else ScheduleCollapse();
    }

    public void Expand(TimeSpan duration)
    {
        var wasVisible = IsVisible;
        var reversing = _collapseCompletion is not null;
        var startY = reversing ? DropTransform.Y : -Height;
        var startOpacity = reversing ? DockContent.Opacity : 0;
        var startShadeOpacity = reversing ? _backdrop.ShadeOpacity : 0;
        var animate = (!IsVisible || reversing) && SystemParameters.ClientAreaAnimation;
        _keepOpenUntil = DateTime.UtcNow + duration;
        if (reversing) ResetTransitionAnimations();
        if (animate)
        {
            ResetTransitionAnimations();
            DropTransform.Y = startY;
            DockContent.Opacity = startOpacity;
            _backdrop.SetShadeOpacity(startShadeOpacity);
        }
        Show();
        PositionAtTop();
        if (!wasVisible || reversing) _hasEnteredHotArea = IsPointerWithinShelf();
        _outsideSince = null;
        if (animate)
        {
            PlayEntranceAnimation(startY, startOpacity);
            _backdrop.FadeShade(1, TimeSpan.FromMilliseconds(220));
        }
        ScheduleCollapse();
    }

    public Task<bool> CollapseAsync(bool cancelOnPointerReturn = false)
    {
        if (_collapseCompletion is not null) return _collapseCompletion.Task;
        if (!IsVisible) return Task.FromResult(true);
        if (!cancelOnPointerReturn) _hideTimer.Stop();
        if (!SystemParameters.ClientAreaAnimation) { Hide(); return Task.FromResult(true); }

        var startY = DropTransform.Y;
        var startOpacity = DockContent.Opacity;
        var startShadeOpacity = _backdrop.ShadeOpacity;
        ResetTransitionAnimations();
        DropTransform.Y = startY;
        DockContent.Opacity = startOpacity;
        _backdrop.SetShadeOpacity(startShadeOpacity);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _collapseCompletion = completion;
        _cancelCollapseOnPointerReturn = cancelOnPointerReturn;
        if (cancelOnPointerReturn) ScheduleCollapse();
        var distance = ActualHeight > 0 ? ActualHeight : Height;
        var rise = new DoubleAnimation(startY, -distance, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        };
        rise.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_collapseCompletion, completion)) return;
            _collapseCompletion = null;
            Hide();
            completion.TrySetResult(true);
        };
        DropTransform.BeginAnimation(TranslateTransform.YProperty, rise, HandoffBehavior.SnapshotAndReplace);
        _backdrop.FadeShade(0, TimeSpan.FromMilliseconds(280));
        DockContent.BeginAnimation(OpacityProperty, new DoubleAnimation(startOpacity, 0, TimeSpan.FromMilliseconds(130))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            FillBehavior = FillBehavior.HoldEnd
        }, HandoffBehavior.SnapshotAndReplace);
        return completion.Task;
    }

    private void PlayEntranceAnimation(double startY, double startOpacity)
    {
        // Base values are the resting state; completed clocks release them automatically.
        DropTransform.Y = 0;
        DockContent.Opacity = 1;
        var fall = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop };
        fall.KeyFrames.Add(new DiscreteDoubleKeyFrame(startY, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fall.KeyFrames.Add(new EasingDoubleKeyFrame(8, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        fall.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        fall.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        DropTransform.BeginAnimation(TranslateTransform.YProperty, fall, HandoffBehavior.SnapshotAndReplace);
        DockContent.BeginAnimation(OpacityProperty, new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetTransitionAnimations()
    {
        var completion = _collapseCompletion;
        _collapseCompletion = null;
        _cancelCollapseOnPointerReturn = false;
        DropTransform.BeginAnimation(TranslateTransform.YProperty, null);
        DockContent.BeginAnimation(OpacityProperty, null);
        DropTransform.Y = 0;
        DockContent.Opacity = 1;
        _backdrop.SetShadeOpacity(1);
        completion?.TrySetResult(false);
    }

    public void PositionAtTop()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var height = (int)(328 * monitor.Scale);
        // Raise the background first so it always remains below cards and toolbar controls.
        DesktopEnvironment.Place(_backdrop, new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, height));
        DesktopEnvironment.Place(this, new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, height));
    }

    public void SetStatus(string text) { StatusLabel.Text = text; _statusTimer.Stop(); _statusTimer.Start(); UpdateHintVisibility(); }
    private void UpdateHintVisibility()
    {
        var showEmpty = _cards.Length == 0 && !_statusTimer.IsEnabled;
        EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Visibility = showEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
    public void ShowCopied(Guid id)
    {
        foreach (var card in _cards) card.IsCopied = card.Item.Id == id;
        _copyTimer.Stop();
        _copyTimer.Start();
    }
    private void ScheduleCollapse() { if (IsVisible && !IsLocked) _hideTimer.Start(); }
    private void CheckHotArea()
    {
        if (!IsVisible || IsLocked) { _hideTimer.Stop(); return; }
        if (IsBusy) { _outsideSince = null; return; }
        if (IsPointerWithinShelf())
        {
            _hasEnteredHotArea = true;
            _outsideSince = null;
            if (_collapseCompletion is not null && _cancelCollapseOnPointerReturn) Expand(TimeSpan.FromSeconds(2));
            return;
        }
        if (_collapseCompletion is not null) return;
        var now = DateTime.UtcNow;
        // The initial preview gets a grace period until the user enters the hot area.
        if (!_hasEnteredHotArea && now < _keepOpenUntil) { _outsideSince = null; return; }
        _outsideSince ??= now;
        if (now - _outsideSince.Value >= TimeSpan.FromMilliseconds(100))
            CollapseRequested?.Invoke(true);
    }
    private bool IsPointerWithinShelf()
    {
        if (!IsVisible) return false;
        var cursor = DesktopEnvironment.Cursor;
        var point = PointFromScreen(new Point(cursor.X, cursor.Y));
        return point.X >= 0 && point.X < ActualWidth && point.Y >= 0 && point.Y < ActualHeight;
    }
    private void CaptureClick(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke();
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    public void RequestCollapse()
    {
        if (IsLocked) { SetLocked(false); LockChanged?.Invoke(false); }
        CollapseRequested?.Invoke(false);
    }
    private void LockClick(object sender, RoutedEventArgs e) { SetLocked(!IsLocked); LockChanged?.Invoke(IsLocked); }
    private void ClearClick(object sender, RoutedEventArgs e)
    {
        if (IsConfirmationOpen || _cards.Length == 0) return;
        IsConfirmationOpen = true;
        try
        {
            var confirmation = new ClearConfirmationWindow(_cards.Length) { Owner = this };
            if (confirmation.ShowDialog() == true) ClearRequested?.Invoke();
        }
        finally { IsConfirmationOpen = false; ScheduleCollapse(); }
    }

    private static bool IsButtonSource(object source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Button) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }

    private void CardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource)) return;
        _pressed = (sender as FrameworkElement)?.Tag as ScreenshotCard;
        _dragStart = e.GetPosition(this);
        _didDrag = false;
        if (e.ClickCount == 2 && _pressed is not null)
        { PreviewRequested?.Invoke(_pressed); _pressed = null; e.Handled = true; }
    }

    private void CardMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is null || _didDrag) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _didDrag = true;
        IsDragging = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, ImageTransferService.CreateDragData(_pressed.FilePath), DragDropEffects.Copy); }
        catch (Exception ex) { Error?.Invoke(ex); }
        finally { IsDragging = false; _pressed = null; ScheduleCollapse(); }
        e.Handled = true;
    }

    private void CardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource)) return;
        if (!_didDrag && _pressed is not null) CopyRequested?.Invoke(_pressed);
        _pressed = null;
    }

    private void DeleteCardClick(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is ScreenshotCard card) DeleteRequested?.Invoke(card); e.Handled = true; }

    private static ScreenshotCard? MenuCard(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement element ? element.Tag as ScreenshotCard : null;
    private void CopyMenuClick(object sender, RoutedEventArgs e) { if (MenuCard(sender) is { } card) CopyRequested?.Invoke(card); }
    private void PreviewMenuClick(object sender, RoutedEventArgs e) { if (MenuCard(sender) is { } card) PreviewRequested?.Invoke(card); }
    private void SaveMenuClick(object sender, RoutedEventArgs e) { if (MenuCard(sender) is { } card) SaveRequested?.Invoke(card); }
    private void DeleteMenuClick(object sender, RoutedEventArgs e) { if (MenuCard(sender) is { } card) DeleteRequested?.Invoke(card); }
    private void CardMenuOpened(object sender, RoutedEventArgs e) { _menuOpen = true; }
    private void CardMenuClosed(object sender, RoutedEventArgs e) { _menuOpen = false; ScheduleCollapse(); }
    private void CardsMouseWheel(object sender, MouseWheelEventArgs e)
    { ScrollCards(e.Delta, false); e.Handled = true; }
    private void ScrollCards(int delta, bool horizontal)
    {
        if (!IsVisible || IsBusy) return;
        var target = Math.Clamp((_pendingWheelOffset ?? CardScroller.HorizontalOffset) + (horizontal ? delta : -delta), 0, CardScroller.ScrollableWidth);
        // A clamped no-op emits no ScrollChanged event, so it must not leave a stale target.
        _pendingWheelOffset = Math.Abs(CardScroller.HorizontalOffset - target) < 0.1 ? null : target;
        CardScroller.ScrollToHorizontalOffset(target);
    }
    private void CardsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 || e.ViewportWidthChange != 0 || (_pendingWheelOffset.HasValue && Math.Abs(CardScroller.HorizontalOffset - _pendingWheelOffset.Value) < 0.1))
            _pendingWheelOffset = null;
    }
    private void StartWheelCapture()
    {
        try { _wheelListener?.Start(); }
        catch (Exception exception) { Error?.Invoke(exception); }
    }
}
