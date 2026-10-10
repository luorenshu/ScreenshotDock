using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static void ClickEditor(CaptureWindow window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static byte[] Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
    private static bool RedAt(BitmapSource image, int x, int y) { var p = Pixel(image, x, y); return p[2] > 230 && p[1] < 120 && p[0] < 120; }
    private static bool BlueAt(BitmapSource image, int x, int y) { var p = Pixel(image, x, y); return p[0] > 220 && p[2] < 100; }
    private static BitmapSource EditorFixture(int width, int height)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 252)), null, new Rect(0, 0, width, height));
            var line = new Pen(new SolidColorBrush(Color.FromRgb(229, 234, 241)), 1);
            for (var x = 0; x < width; x += 40) context.DrawLine(line, new Point(x, 0), new Point(x, height));
            for (var y = 0; y < height; y += 40) context.DrawLine(line, new Point(0, y), new Point(width, y));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing); image.Freeze(); return image;
    }
    private static void DrawEditorStroke(CaptureEditSession editor, PixelPoint start, PixelPoint end, double scale = 1)
    { editor.BeginStroke(start, scale); editor.UpdateStroke(end); editor.EndStroke(); }

    private static async Task RunEditorTestsAsync()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var bounds = DesktopEnvironment.VirtualBounds;
        var frame = new DesktopFrame(EditorFixture(bounds.Width, bounds.Height), bounds);
        var selection = new PixelRect(monitor.Bounds.X + 360, monitor.Bounds.Y + 220, 680, 380);
        var session = new SelectionSession(bounds);
        using var editor = new CaptureEditSession(session);
        var window = new CaptureWindow(frame, monitor, session, editor) { ShowActivated = false, IsHitTestVisible = false };
        // These tests exercise the real controls without competing with the user's desktop mouse.
        DesktopEnvironment.DoNotActivate(window);
        DesktopEnvironment.MakeClickThrough(window);
        window.Show();
        session.SetSelection(selection);
        await Task.Delay(80);
        var toolbar = (Border)window.FindName("CaptureToolbar");
        var location = toolbar.PointToScreen(new Point());
        Check(toolbar.IsVisible && location.Y >= selection.Bottom && Math.Abs(location.X + toolbar.ActualWidth * monitor.Scale - selection.Right) < 3,
            "Annotation toolbar appears below the right edge of the selected region");

        ClickEditor(window, "RectangleToolButton");
        DrawEditorStroke(editor, new(selection.X + 45, selection.Y + 55), new(selection.X + 245, selection.Y + 155));
        var rectangle = AnnotationRenderer.Render(frame, selection, editor.Document.Items);
        Check(editor.Document.Items.Count == 1 && RedAt(rectangle, 45, 95), "Rectangle tool exports a real colored outline");
        ClickEditor(window, "ArrowToolButton");
        DrawEditorStroke(editor, new(selection.X + 300, selection.Y + 70), new(selection.X + 460, selection.Y + 180));
        Check(RedAt(AnnotationRenderer.Render(frame, selection, editor.Document.Items), 380, 125), "Arrow tool renders its shaft into the final image");

        var blue = Descendants(window).OfType<Button>().Single(b => b.Tag as string == "#FF2488FF");
        blue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ClickEditor(window, "PenToolButton");
        editor.BeginStroke(new(selection.X + 70, selection.Y + 240), 1);
        editor.UpdateStroke(new(selection.X + 130, selection.Y + 270));
        editor.UpdateStroke(new(selection.X + 190, selection.Y + 245));
        editor.EndStroke();
        Check(BlueAt(AnnotationRenderer.Render(frame, selection, editor.Document.Items), 130, 270), "Freehand drawing retains the selected color and every path segment");
        ClickEditor(window, "EllipseToolButton");
        DrawEditorStroke(editor, new(selection.X + 500, selection.Y + 220), new(selection.X + 610, selection.Y + 320));
        Check(BlueAt(AnnotationRenderer.Render(frame, selection, editor.Document.Items), 555, 220), "Ellipse tool renders into the final image");

        ClickEditor(window, "TextToolButton");
        editor.BeginStroke(new(selection.X + 320, selection.Y + 260), 1);
        var textbox = (TextBox)window.FindName("AnnotationTextBox");
        Check(textbox.Visibility == Visibility.Visible, "Text tool opens an inline editor at the selected point");
        textbox.Text = "重点区域\n支持标注";
        textbox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(editor.Document.Items[^1].Tool == CaptureTool.Text && editor.Document.Items[^1].Text.Contains("\n") && textbox.Visibility == Visibility.Collapsed,
            "Committing text preserves Chinese text and line breaks");
        var annotated = AnnotationRenderer.Render(frame, selection, editor.Document.Items);
        var textMark = editor.Document.Items[^1];
        var textRegion = new Int32Rect(textMark.Start.X, textMark.Start.Y, Math.Min(140, selection.Width - textMark.Start.X), Math.Min(60, selection.Height - textMark.Start.Y));
        var textData = new byte[textRegion.Width * textRegion.Height * 4];
        new FormatConvertedBitmap(annotated, PixelFormats.Bgra32, null, 0).CopyPixels(textRegion, textData, textRegion.Width * 4, 0);
        var textPixels = 0;
        for (var i = 0; i < textData.Length; i += 4) if (textData[i] > 220 && textData[i + 2] < 100) textPixels++;
        Check(textPixels > 50, "Chinese text is rasterized into exported pixels");

        var count = editor.Document.Items.Count;
        editor.BeginStroke(new(selection.X + 30, selection.Y + 300), 1);
        textbox.Text = "取消这段文字";
        textbox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(editor.Document.Items.Count == count && textbox.Visibility == Visibility.Collapsed && window.IsVisible, "Esc cancels inline text while preserving the screenshot editor");

        ClickEditor(window, "EraserToolButton");
        editor.BeginStroke(new(selection.X + 45, selection.Y + 95), 1);
        Check(editor.Document.Items.Count == count - 1 && !RedAt(AnnotationRenderer.Render(frame, selection, editor.Document.Items), 45, 95),
            "Eraser removes the annotation hit by the cursor");
        ClickEditor(window, "UndoButton");
        Check(editor.Document.Items.Count == count && RedAt(AnnotationRenderer.Render(frame, selection, editor.Document.Items), 45, 95),
            "Toolbar undo restores erased image content");
        ClickEditor(window, "RedoButton");
        Check(editor.Document.Items.Count == count - 1, "Toolbar redo reapplies the removal");
        ClickEditor(window, "UndoButton");
        ClickEditor(window, "TextToolButton");
        await Task.Delay(30);
        var visual = SaveVisual(window, "capture-editor.png");
        var preview = new CroppedBitmap(visual, new Int32Rect(selection.X - monitor.Bounds.X - 22, selection.Y - monitor.Bounds.Y - 42,
            selection.Width + 44, selection.Height + 145));
        File.WriteAllBytes(Path.Combine(_output, "capture-editor-preview.png"), ScreenCaptureService.EncodePng(preview));
        File.WriteAllBytes(Path.Combine(_output, "annotated.png"), ScreenCaptureService.EncodePng(annotated));
        Check(annotated.PixelWidth == selection.Width && annotated.PixelHeight == selection.Height && Pixel(annotated, 1, 1)[3] == 255,
            "Export retains physical image size and excludes toolbar, selection handles and mask");

        session.SetSelection(selection with { X = selection.X + 80, Y = selection.Y + 50 });
        Check(RedAt(AnnotationRenderer.Render(frame, session.Selection, editor.Document.Items), 45, 95), "Moving the selected region keeps annotation coordinates relative to the image");
        session.SetSelection(new(monitor.Bounds.Right - 200, monitor.Bounds.Bottom - 110, 200, 110));
        await Task.Delay(30);
        location = toolbar.PointToScreen(new Point());
        Check(toolbar.IsVisible && location.X >= monitor.Bounds.X && location.Y >= monitor.Bounds.Y
            && location.X + toolbar.ActualWidth * monitor.Scale <= monitor.Bounds.Right && location.Y + toolbar.ActualHeight * monitor.Scale <= monitor.Bounds.Bottom,
            "Toolbar remains accessible when the selection touches screen edges");
        await TestArrowStylesAsync(window, frame, selection, monitor);
        window.Close();

        var negativeFrame = new DesktopFrame(EditorFixture(800, 500), new(-800, -200, 800, 500));
        var negativeSession = new SelectionSession(negativeFrame.Bounds);
        negativeSession.SetSelection(new(-730, -160, 300, 200));
        using var scaled = new CaptureEditSession(negativeSession);
        scaled.SetTool(CaptureTool.Rectangle);
        DrawEditorStroke(scaled, new(-710, -130), new(-590, -60), 1.5);
        var scaledImage = AnnotationRenderer.Render(negativeFrame, negativeSession.Selection, scaled.Document.Items);
        Check(scaled.Document.Items[0].Thickness == 7.5 && scaledImage.PixelWidth == 300 && scaledImage.PixelHeight == 200 && RedAt(scaledImage, 20, 70),
            "Negative monitor coordinates and 150 percent tool sizes export in physical pixels");

        var cacheRoot = Path.Combine(_output, "protected-cache");
        var exports = Path.Combine(_output, "exports");
        Directory.CreateDirectory(cacheRoot); Directory.CreateDirectory(exports);
        var destination = Path.Combine(exports, "annotated.png");
        CaptureFileWriter.SavePng(annotated, destination, cacheRoot);
        Check(RedAt(ScreenCaptureService.LoadImage(destination), 45, 95), "Saving PNG preserves the edited screenshot pixels");
        var forbidden = Path.Combine(cacheRoot, "protected.png");
        File.WriteAllText(forbidden, "fixture-original");
        var rejected = false;
        try { CaptureFileWriter.SavePng(annotated, forbidden, cacheRoot); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllText(forbidden) == "fixture-original" && !Directory.EnumerateFiles(exports, "*.tmp").Any(),
            "PNG save protects cache files and leaves no temporary files behind");

        await RenderThemePreviewAsync();
        await TestEditedCapturePipelineAsync(monitor);
        Check(_application.Windows.Count == 0, "Editor verification closes all capture and tool windows");
        Console.WriteLine($"{Checks.Count} editor integration checks passed.");
    }

    private static async Task TestEditedCapturePipelineAsync(MonitorDescriptor monitor)
    {
        var previousForeground = GetForegroundWindow();
        var root = Path.Combine(_output, "app-data");
        var cache = new ScreenshotRepository(root);
        cache.Load(new(), DateTimeOffset.UtcNow); cache.Clear();
        new SettingsStore(root).Save(new() { AutoCopy = false, AutoExpand = false,
            PinHotkey = TestShortcut(0x7A),
            Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x77, KeyName = "F8" } });
        var saved = Path.Combine(_output, "exports", "pipeline.png");
        var paths = new Queue<string?>([null, Path.Combine(root, "forbidden.png"), saved]);
        using var controller = new AppController(root, _ => paths.Dequeue());
        await controller.StartAsync();
        var fixture = new Window { Title = "ScreenshotDock editor fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, Topmost = true, Width = 600, Height = 330, Background = new SolidColorBrush(Color.FromRgb(40, 120, 75)) };
        fixture.Show();
        var fixtureBounds = new PixelRect(monitor.Bounds.X + 300, monitor.Bounds.Y + 360, 600, 330);
        DesktopEnvironment.Place(fixture, fixtureBounds);
        await Task.Delay(80);
        var selection = new PixelRect(fixtureBounds.X + 20, fixtureBounds.Y + 20, 500, 260);
        async Task<CaptureWindow> Ready()
        {
            for (var i = 0; i < 100 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(10);
            foreach (var overlay in _application.Windows.OfType<CaptureWindow>())
            {
                DesktopEnvironment.MakeClickThrough(overlay);
                DesktopEnvironment.DoNotActivate(overlay);
                overlay.IsHitTestVisible = false;
            }
            SetForegroundWindow(previousForeground);
            var window = _application.Windows.OfType<CaptureWindow>().First(w => w.Left < monitor.Bounds.Right / monitor.Scale);
            window.Editor.Selection.SetSelection(selection);
            await Task.Delay(20);
            return window;
        }

        try
        {
            var capture = controller.CaptureAsync();
            var window = await Ready();
            ClickEditor(window, "RectangleToolButton");
            DrawEditorStroke(window.Editor, new(selection.X + 40, selection.Y + 50), new(selection.X + 220, selection.Y + 150));
            ClickEditor(window, "ArrowToolButton");
            ClickEditor(window, "ArrowOutlineStyleButton");
            DrawEditorStroke(window.Editor, new(selection.X + 310, selection.Y + 70), new(selection.X + 450, selection.Y + 180));
            ClickEditor(window, "SaveCaptureButton");
            await Task.Delay(30);
            Check(!capture.IsCompleted && window.IsVisible && window.Editor.Document.Items.Count == 2, "Canceling the save dialog preserves editable annotations");
            ClickEditor(window, "SaveCaptureButton");
            await Task.Delay(80);
            Check(!capture.IsCompleted && !window.Editor.IsBusy && window.Editor.Status.Contains("暂存目录"), "A rejected save keeps the editor open with a recoverable error");
            ClickEditor(window, "SaveCaptureButton");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Load(new(), DateTimeOffset.UtcNow);
            Check(File.Exists(saved) && cache.Items.Count == 1 && RedAt(ScreenCaptureService.LoadImage(saved), 40, 95),
                "Save toolbar action exports annotations and adds the same image to the screenshot cache");
            Check(File.ReadAllBytes(saved).SequenceEqual(File.ReadAllBytes(cache.GetImagePath(cache.Items[0].Id))), "Saved PNG and cached PNG contain identical edited image data");
            var savedImage = ScreenCaptureService.LoadImage(saved);
            Check(RedAt(savedImage, 402, 149) && !RedAt(savedImage, 406, 145), "Save and cache retain the hollow shaft of the selected outline arrow");
            Check(!_application.Windows.OfType<CaptureWindow>().Any(), "Saving a capture removes every monitor overlay");

            System.Windows.Clipboard.SetText("editor fixture");
            capture = controller.CaptureAsync();
            window = await Ready();
            ClickEditor(window, "ArrowToolButton");
            ClickEditor(window, "ArrowSolidStyleButton");
            ((ComboBox)window.FindName("StrokeWidthBox")).SelectedIndex = 3;
            DrawEditorStroke(window.Editor, new(selection.X + 50, selection.Y + 50), new(selection.X + 250, selection.Y + 150));
            ClickEditor(window, "TextToolButton");
            window.Editor.BeginStroke(new(selection.X + 300, selection.Y + 170), 1);
            var textbox = (TextBox)window.FindName("AnnotationTextBox");
            textbox.Text = "直接复制\n中文文字";
            ((ComboBox)window.FindName("FontSizeBox")).SelectedIndex = 2;
            Check(textbox.Visibility == Visibility.Visible && textbox.FontSize == 24 && window.Editor.FontSize == 24,
                "Font size changes update the active text editor before committing");
            ClickEditor(window, "CopyCaptureButton");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            var clipboard = System.Windows.Clipboard.GetImage();
            Check(clipboard?.PixelWidth == 500 && RedAt(clipboard, 150, 100), "Copy toolbar action includes annotations even when automatic copying is disabled");
            Check(RedAt(clipboard!, 230, 146), "Copy preserves the filled arrowhead selected in the toolbar");
            var glyphs = new byte[180 * 65 * 4];
            new FormatConvertedBitmap(clipboard!, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(305, 175, 180, 65), glyphs, 180 * 4, 0);
            var redGlyphs = 0;
            for (var i = 0; i < glyphs.Length; i += 4) if (glyphs[i + 2] > 230 && glyphs[i + 1] < 120 && glyphs[i] < 120) redGlyphs++;
            Check(redGlyphs > 50, "Copy commits pending inline text and includes Chinese glyphs in the clipboard image");
            cache.Load(new(), DateTimeOffset.UtcNow);
            var count = cache.Items.Count;
            capture = controller.CaptureAsync();
            window = await Ready();
            ClickEditor(window, "CancelCaptureButton");
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Load(new(), DateTimeOffset.UtcNow);
            Check(cache.Items.Count == count, "Canceling the editor does not add a screenshot to the cache");

            foreach (var state in new[] { "Before selection", "During drawing", "Inside text editor", "Over toolbar" })
            {
                capture = controller.CaptureAsync();
                window = await Ready();
                UIElement target = ((Grid)window.FindName("Surface")).Children[0];
                if (state == "Before selection") window.Editor.Selection.SetSelection(default);
                else if (state == "During drawing")
                {
                    ClickEditor(window, "PenToolButton");
                    window.Editor.BeginStroke(new(selection.X + 50, selection.Y + 50), 1);
                    window.Editor.UpdateStroke(new(selection.X + 160, selection.Y + 90));
                }
                else if (state == "Inside text editor")
                {
                    ClickEditor(window, "TextToolButton");
                    window.Editor.BeginStroke(new(selection.X + 30, selection.Y + 30), 1);
                    var pendingText = (TextBox)window.FindName("AnnotationTextBox");
                    pendingText.Text = "右键取消，不保存";
                    target = pendingText;
                }
                else target = (Button)window.FindName("SaveCaptureButton");
                var down = new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
                var up = new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent };
                target.RaiseEvent(down);
                target.RaiseEvent(up);
                await capture.WaitAsync(TimeSpan.FromSeconds(5));
                cache.Load(new(), DateTimeOffset.UtcNow);
                Check(down.Handled && up.Handled && cache.Items.Count == count && !_application.Windows.OfType<CaptureWindow>().Any(),
                    $"Right click cancels the entire capture without saving or opening a menu: {state}");
            }
        }
        finally { fixture.Close(); controller.Dispose(); }
    }

    private static async Task TestArrowStylesAsync(CaptureWindow window, DesktopFrame frame, PixelRect selection, MonitorDescriptor monitor)
    {
        var editor = window.Editor;
        editor.Selection.SetSelection(selection);
        editor.Document.Reset();
        editor.ColorArgb = 0xFFFF4D4F;
        ((ComboBox)window.FindName("StrokeWidthBox")).SelectedIndex = 2;
        ClickEditor(window, "ArrowToolButton");
        var options = (StackPanel)window.FindName("ArrowStyleOptions");
        Check(options.Visibility == Visibility.Visible, "Arrow tool exposes five illustrated style choices");
        var styles = new[] { (ArrowStyle.Line, "ArrowLineStyleButton", "细线", new PixelPoint(40,110), new PixelPoint(250,35)),
            (ArrowStyle.Solid, "ArrowSolidStyleButton", "实心", new PixelPoint(355,110), new PixelPoint(610,35)),
            (ArrowStyle.Tapered, "ArrowTaperedStyleButton", "渐尖", new PixelPoint(40,225), new PixelPoint(250,145)),
            (ArrowStyle.Outline, "ArrowOutlineStyleButton", "空心", new PixelPoint(355,225), new PixelPoint(610,145)),
            (ArrowStyle.Polyline, "ArrowPolylineStyleButton", "折线", new PixelPoint(60,320), new PixelPoint(600,285)) };
        var marks = new List<ScreenshotAnnotation>();
        var fingerprints = new List<byte[]>();
        foreach (var (style, button, label, start, end) in styles)
        {
            ClickEditor(window, button);
            Check(editor.ArrowStyle == style && ((SolidColorBrush)((Button)window.FindName(button)).Background).Color == Color.FromRgb(213,233,255),
                $"Selecting {style} highlights the choice and updates the next arrow");
            editor.BeginStroke(new(selection.X + start.X, selection.Y + start.Y), 1);
            if (style == ArrowStyle.Polyline)
            {
                foreach (var point in new[] { new PixelPoint(150,355), new(360,355), new(430,300) })
                    editor.UpdateStroke(new(selection.X + point.X, selection.Y + point.Y));
            }
            editor.UpdateStroke(new(selection.X + end.X, selection.Y + end.Y)); editor.EndStroke();
            var mark = editor.Document.Items[^1];
            marks.Add(mark);
            var normalized = mark with { Start = new(70,220), End = new(330,100),
                Points = style == ArrowStyle.Polyline ? [new(70,220), new(120,280), new(230,240), new(330,100)] : default };
            fingerprints.Add(ScreenCaptureService.EncodePng(AnnotationRenderer.Render(frame, selection, [normalized])));
            editor.Document.Add(new(CaptureTool.Text, new(start.X, start.Y + 12), new(start.X,start.Y+12), 0xFF344254, 3, 16, label));
        }
        Check(marks.Select(a => a.ArrowStyle).SequenceEqual(Enum.GetValues<ArrowStyle>())
            && fingerprints.Select(Convert.ToBase64String).Distinct().Count() == 5, "Five arrow styles produce distinct exported pixels and keep their individual styles");
        Check(marks[^1].Points.Length == 5 && AnnotationRenderer.HitTest(marks[^1], new(250,355), 1),
            "Polyline arrows preserve intermediate turns and support erasing their route");

        var solid = new ScreenshotAnnotation(CaptureTool.Arrow, new(30,100), new(300,100), 0xFFFF4D4F, 8, ArrowStyle: ArrowStyle.Solid);
        var taper = solid with { ArrowStyle = ArrowStyle.Tapered };
        var outline = solid with { ArrowStyle = ArrowStyle.Outline };
        Check(AnnotationRenderer.HitTest(solid, new(280,106), 0) && AnnotationRenderer.HitTest(taper, new(280,106), 0),
            "Filled and tapered arrowheads can be erased by clicking their interiors");
        Check(!AnnotationRenderer.HitTest(outline, new(160,100), 0) && AnnotationRenderer.HitTest(outline, new(160,109), 0),
            "Outline arrow hit testing follows its visible border and leaves the hollow interior empty");
        foreach (var style in Enum.GetValues<ArrowStyle>())
            foreach (var end in new[] { new PixelPoint(203,200), new(197,200), new(200,197), new(200,203) })
            {
                var tiny = solid with { Start = new(200,200), End = end, ArrowStyle = style,
                    Points = [new(200,200),end] };
                var image = AnnotationRenderer.Render(frame, selection, [tiny]);
                if (image.PixelWidth != selection.Width) throw new InvalidOperationException("Invalid short arrow dimensions");
            }
        Check(true, "Every arrow style handles very short arrows in all four directions");

        var latest = editor.Document.Items.ToArray();
        ClickEditor(window, "UndoButton"); ClickEditor(window, "RedoButton");
        Check(editor.Document.Items.SequenceEqual(latest), "Undo and redo retain each arrow style and polyline path");
        var imageResult = AnnotationRenderer.Render(frame, selection, editor.Document.Items);
        File.WriteAllBytes(Path.Combine(_output, "arrow-styles.png"), ScreenCaptureService.EncodePng(imageResult));
        await Task.Delay(30);
        var visual = SaveVisual(window, "capture-arrow-styles.png");
        var preview = new CroppedBitmap(visual, new Int32Rect(selection.X - monitor.Bounds.X - 22, selection.Y - monitor.Bounds.Y - 42,
            selection.Width + 44, selection.Height + 185));
        File.WriteAllBytes(Path.Combine(_output, "arrow-toolbar-preview.png"), ScreenCaptureService.EncodePng(preview));
        ClickEditor(window, "RectangleToolButton");
        Check(options.Visibility == Visibility.Collapsed && editor.ArrowStyle == ArrowStyle.Polyline, "Other tools hide arrow style choices while preserving the last choice");
    }
}
