using Microsoft.Win32;
using Serilog;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.ViewModels;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Clipboard;
using ScreenshotDock.Windows.Hotkeys;
using ScreenshotDock.Windows.Native;
using ScreenshotDock.Windows.Tray;
using ScreenshotDock.Windows.Startup;

namespace ScreenshotDock.App;

public sealed class AppController : IDisposable
{
    private readonly ScreenshotRepository _repository;
    private readonly SettingsStore _settingsStore;
    private readonly GlobalHotkeyService _hotkey;
    private readonly DockWindow _dock = new();
    private readonly EdgeWindow _edge;
    private readonly TrayService _tray;
    private readonly IStartupRegistration _startup;
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _cleanupTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly List<CaptureWindow> _overlays = [];
    private readonly List<PreviewWindow> _previews = [];
    private readonly List<PinnedImageWindow> _pinnedImages = [];
    private readonly Dispatcher _dispatcher;
    private readonly Func<Window, string?> _captureSavePathPicker;
    private AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _capturing;
    private bool _saving;
    private bool _disposed;

    public AppController(string root, Func<Window, string?>? captureSavePathPicker = null, Func<EdgeWindow>? edgeFactory = null, IStartupRegistration? startup = null)
    {
        _dispatcher = Application.Current.Dispatcher;
        _edge = edgeFactory?.Invoke() ?? new();
        _startup = startup ?? new StartupRegistration();
        _captureSavePathPicker = captureSavePathPicker ?? ChooseCaptureSavePath;
        _settingsStore = new(root);
        _settings = _settingsStore.Load();
        _repository = new(root) { OnError = exception => Log.Warning(exception, "Cache reconciliation / cleanup") };
        _hotkey = new();
        using var icon = typeof(AppController).Assembly.GetManifestResourceStream("ScreenshotDock.AppIcon.ico")
            ?? throw new InvalidOperationException("缺少应用图标资源。");
        _tray = new(icon);
        _hotkey.Pressed += (_, _) => _ = CaptureAsync();
        _tray.CaptureRequested += () => _ = CaptureAsync();
        _tray.DockRequested += () => ExpandDock(manual: true);
        _tray.SettingsRequested += ShowSettings;
        _tray.PauseRequested += TogglePause;
        _tray.ExitRequested += () => Application.Current.Shutdown();
        _dock.CaptureRequested += () => _ = CaptureAsync();
        _dock.SettingsRequested += ShowSettings;
        _dock.CollapseRequested += CollapseDock;
        _dock.CopyRequested += card => _ = CopyAsync(card);
        _dock.PreviewRequested += Preview;
        _dock.SaveRequested += Save;
        _dock.DeleteRequested += card => _ = DeleteAsync(card);
        _dock.ClearRequested += () => _ = ClearAsync();
        _dock.LockChanged += locked =>
        {
            _settings = _settings with { DockLocked = locked };
            try { _settingsStore.Save(_settings); } catch (Exception e) { ReportError(e); }
        };
        _dock.Error += ReportError;
        _edge.ExpandRequested += () => ExpandDock(manual: false);
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        _cleanupTimer.Tick += async (_, _) =>
        {
            if (_capturing || _saving || _dock.IsBusy) return;
            try { await Task.Run(() => _repository.Cleanup(_settings.Storage, DateTimeOffset.UtcNow)); if (!_disposed) RefreshCards(); }
            catch (Exception ex) { Log.Warning(ex, "Periodic cleanup failed"); }
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    public async Task StartAsync(bool showInitialDock = true)
    {
        await Task.Run(() => _repository.Load(_settings.Storage, DateTimeOffset.UtcNow));
        if (_disposed) return;
        _dock.Configure(_settings);
        RefreshCards();
        _edge.Show();
        _edge.PositionAtTop();
        if (!_hotkey.TryRegister(_settings.Hotkey, out var error))
        { _tray.Notify(error); ShowSettings(); _settingsWindow?.ShowError(error); }
        _fullscreenTimer.Start();
        _cleanupTimer.Start();
        if (showInitialDock) ExpandDock(manual: false, duration: TimeSpan.FromSeconds(5));
        Log.Information("ScreenshotDock started; restored {Count} screenshots", _repository.Items.Count);
    }

    private bool IsFullscreen => _settings.SuppressInFullscreen && DesktopEnvironment.IsFullscreenForeground();
    private void ExpandDock(bool manual, TimeSpan? duration = null)
    {
        if (_capturing || _disposed || (!manual && IsFullscreen)) return;
        _edge.Hide();
        _dock.Expand(duration ?? TimeSpan.FromSeconds(manual ? 5 : 2));
    }

    private async void CollapseDock(bool automatic)
    {
        if (_disposed || _capturing || _dock.IsBusy) return;
        var collapsed = await _dock.CollapseAsync(cancelOnPointerReturn: automatic);
        if (collapsed && !_disposed && !_capturing && !_dock.IsVisible && !IsFullscreen)
        { _edge.Show(); _edge.PositionAtTop(); }
    }

    private void CheckFullscreen()
    {
        if (_capturing || _disposed) return;
        if (IsFullscreen)
        {
            _edge.Hide();
            if (!_dock.IsBusy) _dock.Hide();
        }
        else if (!_dock.IsVisible)
        {
            if (_dock.IsLocked) ExpandDock(manual: false);
            else if (!_edge.IsVisible) { _edge.Show(); _edge.PositionAtTop(); }
        }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (_overlays.Count > 0) _overlays[0].Close(); // Layout changes invalidate the frozen desktop frame.
        if (_dock.IsVisible) _dock.PositionAtTop();
        if (_edge.IsVisible) _edge.PositionAtTop();
    });

    public async Task CaptureAsync()
    {
        if (_capturing || _saving || _disposed || _dock.IsConfirmationOpen) return;
        _capturing = true;
        var dockWasOpen = _dock.IsVisible;
        var restoreWindows = new List<Window>();
        PinnedImageWindow? newPinnedImage = null;
        try
        {
            _dock.Hide();
            _edge.Hide();
            foreach (var window in _previews.Cast<Window>().Concat(_pinnedImages).Append(_settingsWindow).Where(w => w is not null && w.IsVisible))
            { restoreWindows.Add(window!); window!.Hide(); }
            // Let DWM commit hidden app windows before BitBlt reads the desktop.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            DesktopEnvironment.FlushComposition();
            var frame = ScreenCaptureService.CaptureDesktop();
            var capture = await SelectAsync(frame);
            if (capture is null)
            {
                if (dockWasOpen || _dock.IsLocked) ExpandAfterCapture();
                return;
            }
            _saving = true;
            var image = capture.Image;
            var storage = _settings.Storage;
            var result = await Task.Run(() => _repository.Add(ScreenCaptureService.EncodePng(image), ScreenCaptureService.Thumbnail(image), storage, DateTimeOffset.UtcNow));
            RefreshCards();
            if (capture.PinBounds is { } pinBounds)
            {
                newPinnedImage = new(image, pinBounds);
                _pinnedImages.Add(newPinnedImage);
                newPinnedImage.Closed += (_, _) => _pinnedImages.Remove(newPinnedImage);
            }
            var copied = false;
            if (_settings.AutoCopy || capture.ForceCopy)
            {
                try { await ImageTransferService.CopyAsync(image); copied = true; }
                catch (Exception ex) { Log.Warning(ex, "Clipboard busy"); _tray.Notify("截图已暂存，但剪贴板正被占用。稍后单击卡片可再次复制。"); }
            }
            _dock.SetStatus(capture.PinBounds.HasValue ? "已贴到屏幕并收纳 · 点击选中后按 Esc 移除贴图"
                : capture.SavedPath is not null ? copied ? "图片已保存、已收纳，也已复制" : "图片已保存并收纳"
                : result.IsDuplicate ? "已有相同截图，已移至最前" : copied ? "已收纳，也已复制到剪贴板" : "已收纳到截图栏");
            if (copied) _dock.ShowCopied(result.Item.Id);
            if (!capture.PinBounds.HasValue && (_settings.AutoExpand || _dock.IsLocked)) ExpandAfterCapture();
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            CloseOverlays();
            _capturing = false;
            _saving = false;
            if (!_disposed)
            {
                foreach (var window in restoreWindows.Where(w => Application.Current.Windows.Cast<Window>().Contains(w))) window.Show();
                if (!_dock.IsVisible && !IsFullscreen) { _edge.Show(); _edge.PositionAtTop(); }
                newPinnedImage?.Show();
                newPinnedImage?.Activate();
            }
        }
    }

    private void ExpandAfterCapture()
    {
        if (IsFullscreen) return;
        _edge.Hide();
        _dock.Expand(TimeSpan.FromSeconds(3));
    }

    private static string? ChooseCaptureSavePath(Window owner)
    {
        var dialog = new SaveFileDialog { Title = "保存标注截图", Filter = "PNG 图片|*.png", FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            AddExtension = true, DefaultExt = ".png", OverwritePrompt = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private Task<CaptureResult?> SelectAsync(DesktopFrame frame)
    {
        var completion = new TaskCompletionSource<CaptureResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SelectionSession(frame.Bounds);
        var editor = new CaptureEditSession(session);
        var finishing = false;
        foreach (var monitor in DesktopEnvironment.Monitors)
        {
            var window = new CaptureWindow(frame, monitor, session, editor);
            window.CancelRequested += () => completion.TrySetResult(null);
            window.CompleteRequested += () => _ = FinishAsync(window, false, false);
            window.CopyRequested += () => _ = FinishAsync(window, true, false);
            window.SaveRequested += () => _ = FinishAsync(window, false, true);
            window.PinRequested += () => _ = FinishAsync(window, false, false, pin: true);
            window.Closed += (_, _) => completion.TrySetResult(null);
            _overlays.Add(window);
            window.Show();
        }
        _overlays.FirstOrDefault(w => DesktopEnvironment.Monitors[_overlays.IndexOf(w)].Bounds.Contains(DesktopEnvironment.Cursor))?.Activate();
        return FinishSelectionAsync();

        async Task FinishAsync(Window owner, bool forceCopy, bool save, bool pin = false)
        {
            if (finishing || completion.Task.IsCompleted || session.Selection.Width < 2 || session.Selection.Height < 2) return;
            finishing = true;
            editor.CommitText();
            if (editor.IsDrawing) editor.EndStroke();
            editor.EndMosaicMove();
            editor.SetBusy(true);
            try
            {
                var path = save ? _captureSavePathPicker(owner) : null;
                if (save && string.IsNullOrWhiteSpace(path)) return;
                var image = AnnotationRenderer.Render(frame, session.Selection, editor.Document.Items);
                if (path is not null) await Task.Run(() => CaptureFileWriter.SavePng(image, path, _repository.Root));
                completion.TrySetResult(new(image, forceCopy, path, pin ? session.Selection : null));
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Cannot finish annotated capture");
                editor.SetStatus($"操作未完成：{exception.Message}");
            }
            finally { if (!completion.Task.IsCompleted) { finishing = false; editor.SetBusy(false); } }
        }

        async Task<CaptureResult?> FinishSelectionAsync()
        {
            var result = await completion.Task;
            CloseOverlays();
            editor.Dispose();
            return result;
        }
    }

    private void CloseOverlays() { foreach (var overlay in _overlays.ToArray()) overlay.Close(); _overlays.Clear(); }

    private void RefreshCards()
    {
        var cards = new List<ScreenshotCard>();
        foreach (var item in _repository.Items)
        {
            try { cards.Add(new(item, _repository)); }
            catch (Exception e) { Log.Warning(e, "Cannot decode cached screenshot {Id}", item.Id); }
        }
        _dock.SetCards(cards);
    }

    private async Task CopyAsync(ScreenshotCard card)
    {
        try { await ImageTransferService.CopyAsync(ScreenCaptureService.LoadImage(card.FilePath)); _dock.SetStatus("已复制到剪贴板"); _dock.ShowCopied(card.Item.Id); }
        catch (Exception ex) { ReportError(ex); }
    }

    private void Preview(ScreenshotCard card)
    {
        try
        {
            var preview = new PreviewWindow(card);
            preview.CopyRequested += item => _ = CopyAsync(item);
            preview.SaveRequested += Save;
            preview.Closed += (_, _) => _previews.Remove(preview);
            _previews.Add(preview);
            preview.Show();
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private void Save(ScreenshotCard card)
    {
        var dialog = new SaveFileDialog { Title = "保存截图", Filter = "PNG 图片|*.png", FileName = $"Screenshot_{card.Item.CreatedAt.ToLocalTime():yyyyMMdd_HHmmss}.png", AddExtension = true, DefaultExt = ".png" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var destination = Path.GetFullPath(dialog.FileName);
            var cacheRoot = Path.GetFullPath(_repository.Root) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            { _tray.Notify("请将永久副本保存到暂存目录之外，例如桌面或图片文件夹。"); return; }
            File.Copy(card.FilePath, destination, overwrite: true);
            _dock.SetStatus("图片已保存到所选位置");
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task DeleteAsync(ScreenshotCard card)
    {
        try { await Task.Run(() => _repository.Delete(card.Item.Id)); RefreshCards(); _dock.SetStatus("已移除暂存图片"); }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ClearAsync()
    {
        try { await Task.Run(_repository.Clear); RefreshCards(); _dock.SetStatus("暂存栏已清空"); }
        catch (Exception ex) { ReportError(ex); }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Show(); _settingsWindow.Activate(); return; }
        _settingsWindow = new(_settings, ApplySettingsAsync, _startup);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private async Task<string?> ApplySettingsAsync(AppSettings updated)
    {
        var previous = _settings;
        // Settings window may have been open while dock lock changed.
        updated = updated with { DockLocked = previous.DockLocked };
        if (!_hotkey.TryRegister(updated.Hotkey, out var error)) return error;
        try { _settingsStore.Save(updated); }
        catch (Exception e)
        {
            _hotkey.TryRegister(previous.Hotkey, out _);
            Log.Error(e, "Cannot persist settings");
            return $"设置保存失败：{e.Message}";
        }
        _settings = updated;
        _dock.Configure(updated);
        await Task.Run(() => _repository.Cleanup(updated.Storage, DateTimeOffset.UtcNow));
        RefreshCards();
        CheckFullscreen();
        return null;
    }

    private void TogglePause()
    {
        if (!_hotkey.SetPaused(!_hotkey.IsPaused, out var error)) _tray.Notify(error);
        _tray.SetPaused(_hotkey.IsPaused);
        _dock.SetStatus(_hotkey.IsPaused ? "快捷键已暂停，仍可点击按钮截图" : "快捷键已恢复");
    }

    public void ReportError(Exception exception)
    {
        Log.Error(exception, "ScreenshotDock operation failed");
        _dock.SetStatus("操作未完成，可查看本地日志");
        _tray.Notify($"操作未完成：{exception.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _fullscreenTimer.Stop();
        _cleanupTimer.Stop();
        CloseOverlays();
        foreach (var preview in _previews.ToArray()) preview.Close();
        foreach (var pinned in _pinnedImages.ToArray()) pinned.Close();
        _settingsWindow?.Close();
        _dock.Close();
        _edge.Close();
        _hotkey.Dispose();
        _tray.Dispose();
    }
}
