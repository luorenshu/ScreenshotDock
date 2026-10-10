using System;
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
    private static BitmapSource PixelPattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                pixels[i] = (byte)((x * 19 + y * 7) % 256);
                pixels[i + 1] = (byte)((x * 13 + y * 29) % 256);
                pixels[i + 2] = (byte)((x * 37 + y * 11) % 256);
                pixels[i + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private static void InlineTextKey(CaptureWindow window, Key key) =>
        ((TextBox)window.FindName("AnnotationTextBox")).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static async Task RunAnnotationFeatureTestsAsync()
    {
        var source = PixelPattern(93, 67);
        var frame = new DesktopFrame(source, new(-93, -67, 93, 67));
        var mosaic = new ScreenshotAnnotation(CaptureTool.Mosaic, new(5, 7), new(85, 59), 0, 5, MosaicBlockSize: 16);
        var image = AnnotationRenderer.Render(frame, frame.Bounds, [mosaic]);
        File.WriteAllBytes(Path.Combine(_output, "mosaic-pixels.png"), ScreenCaptureService.EncodePng(image));
        Check(Pixel(image, 20, 20).SequenceEqual(Pixel(image, 29, 29)), $"Mosaic replaces fine details with uniform physical pixel blocks: {string.Join(',', Pixel(image, 20, 20))} / {string.Join(',', Pixel(image, 29, 29))}");
        var expected = new int[4];
        for (var y = 16; y < 32; y++) for (var x = 16; x < 32; x++)
        {
            var p = Pixel(source, x, y);
            for (var channel = 0; channel < 4; channel++) expected[channel] += p[channel];
        }
        Check(Pixel(image, 20, 20).SequenceEqual(expected.Select(v => (byte)(v / 256))),
            "Mosaic uses the block's average color rather than leaving the original details intact");
        Check(Pixel(image, 4, 20).SequenceEqual(Pixel(source, 4, 20)) && Pixel(image, 85, 20).SequenceEqual(Pixel(source, 85, 20))
            && Pixel(image, 20, 6).SequenceEqual(Pixel(source, 20, 6)), "Mosaic does not alter pixels outside its selected rectangle");
        Check(ScreenCaptureService.EncodePng(image).SequenceEqual(ScreenCaptureService.EncodePng(AnnotationRenderer.Render(frame, frame.Bounds,
            [mosaic with { Start = mosaic.End, End = mosaic.Start }]))), "Dragging a mosaic rectangle in reverse produces the same pixels");
        var clipped = AnnotationRenderer.Render(frame, frame.Bounds, [mosaic with { Start = new(-20, -10), End = new(120, 90) }]);
        Check(clipped.PixelWidth == 93 && clipped.PixelHeight == 67 && Pixel(clipped, 92, 66)[3] == 255,
            "Mosaic clips safely at negative-coordinate selections and partial edge blocks");
        Check(AnnotationRenderer.HitTest(mosaic, new(20, 20), 0) && !AnnotationRenderer.HitTest(mosaic, new(90, 20), 0),
            "Mosaic can be erased by clicking its interior and ignores points outside the region");
        var document = new AnnotationDocument(); document.Add(mosaic); document.RemoveAt(0); document.Undo();
        Check(ScreenCaptureService.EncodePng(AnnotationRenderer.Render(frame, frame.Bounds, document.Items)).SequenceEqual(ScreenCaptureService.EncodePng(image)),
            "Undoing a mosaic removal restores identical exported pixels");
        File.WriteAllBytes(Path.Combine(_output, "mosaic-pixels.png"), ScreenCaptureService.EncodePng(image));

        TestMosaicMovement();
        TestMosaicResizing();
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var bounds = DesktopEnvironment.VirtualBounds;
        var selection = new PixelRect(monitor.Bounds.X + 350, monitor.Bounds.Y + 210, 720, 410);
        var visualFrame = new DesktopFrame(AnnotationSample(bounds, selection), bounds);
        var session = new SelectionSession(bounds);
        using var editor = new CaptureEditSession(session);
        var window = new CaptureWindow(visualFrame, monitor, session, editor) { ShowActivated = false, IsHitTestVisible = false };
        DesktopEnvironment.DoNotActivate(window); DesktopEnvironment.MakeClickThrough(window);
        window.Show(); session.SetSelection(selection);
        try
        {
            await Task.Delay(60);
            ClickEditor(window, "MosaicToolButton");
            Check(editor.Tool == CaptureTool.Mosaic && ((StackPanel)window.FindName("MosaicOptions")).IsVisible
                && !((StackPanel)window.FindName("AnnotationOptions")).IsVisible,
                "Mosaic toolbar exposes particle size and hides irrelevant color and stroke controls");
            ((ComboBox)window.FindName("MosaicSizeBox")).SelectedIndex = 2;
            DrawEditorStroke(editor, new(selection.X + 150, selection.Y + 85), new(selection.X + 550, selection.Y + 160));
            Check(editor.Document.Items[0] is { Tool: CaptureTool.Mosaic, MosaicBlockSize: 24 },
                "Drawing a mosaic records its rectangle and the selected particle size");
            await Task.Delay(40);
            var visual = SaveVisual(window, "mosaic-editor.png");
            SaveAnnotationPreview(visual, selection, monitor, "mosaic-toolbar-preview.png");
            var previewSample = Pixel(visual, selection.X - monitor.Bounds.X + 210, selection.Y - monitor.Bounds.Y + 115);
            Check(previewSample.SequenceEqual(Pixel(AnnotationRenderer.Render(visualFrame, selection, editor.Document.Items), 210, 115)),
                "Live mosaic preview matches the pixels used for image export");

            var topPixels = Enumerable.Range(160, 40).Select(x => Pixel(visual,
                selection.X - monitor.Bounds.X + x, selection.Y - monitor.Bounds.Y + 85)).ToArray();
            Check(topPixels.Any(p => p.Take(3).All(v => v >= 248)) && topPixels.Any(p => p[2] < 245),
                "The active mosaic has a white dashed editing boundary with visible gaps");
            var export = AnnotationRenderer.Render(visualFrame, selection, editor.Document.Items);
            Check(!Enumerable.Range(160, 40).Any(x => Pixel(export, x, 85).Take(3).All(v => v >= 248)),
                "White mosaic editing dashes do not appear in the exported image");
            Check(Pixel(visual, selection.X - monitor.Bounds.X + 150, selection.Y - monitor.Bounds.Y + 85)
                .Take(3).SequenceEqual(new byte[] { 41, 37, 35 })
                && !Pixel(export, 150, 85).Take(3).SequenceEqual(new byte[] { 41, 37, 35 }),
                "Small outlined mosaic handles appear in the editor and are excluded from export");

            var resizeCorner = new PixelPoint(selection.X + 550, selection.Y + 160);
            Check(editor.TryBeginMosaicResize(resizeCorner, monitor.Scale), "A selected mosaic exposes its bottom-right resize handle in the editor");
            editor.UpdateMosaicMove(new(resizeCorner.X + 80, resizeCorner.Y + 55));
            await Task.Delay(30);
            var resizedVisual = SaveVisual(window, "mosaic-resizing.png");
            SaveAnnotationPreview(resizedVisual, selection, monitor, "mosaic-resizing-preview.png");
            var resizedExport = AnnotationRenderer.Render(visualFrame, selection, [editor.MosaicMovePreview!]);
            Check(Pixel(resizedVisual, selection.X - monitor.Bounds.X + 590, selection.Y - monitor.Bounds.Y + 185)
                .SequenceEqual(Pixel(resizedExport, 590, 185)), "Live resizing extends mosaic coverage using the same pixels as image export");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!,
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(!editor.IsMovingMosaic && editor.Document.Items[0].End == new PixelPoint(550, 160) && window.IsVisible,
                "Esc cancels a mosaic resize while keeping the screenshot active");

            var originalMosaic = editor.Document.Items[0];
            var movePoint = new PixelPoint(selection.X + 320, selection.Y + 120);
            Check(editor.TryBeginMosaicMove(movePoint, monitor.Scale), "An existing mosaic can be selected for dragging from the toolbar tool");
            editor.UpdateMosaicMove(new(movePoint.X + 80, movePoint.Y));
            await Task.Delay(30);
            var movingVisual = SaveVisual(window, "mosaic-moving.png");
            SaveAnnotationPreview(movingVisual, selection, monitor, "mosaic-moving-preview.png");
            var movingExport = AnnotationRenderer.Render(visualFrame, selection, [editor.MosaicMovePreview!]);
            Check(Pixel(movingVisual, selection.X - monitor.Bounds.X + 260, selection.Y - monitor.Bounds.Y + 115)
                .SequenceEqual(Pixel(movingExport, 260, 115)) && Pixel(movingVisual,
                selection.X - monitor.Bounds.X + 180, selection.Y - monitor.Bounds.Y + 115)
                .SequenceEqual(Pixel(ScreenCaptureService.Crop(visualFrame, selection), 180, 115)),
                "Dragging updates the live mosaic at its new position and restores the original pixels at its old position");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!,
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            Check(!editor.IsMovingMosaic && editor.Document.Items[0] == originalMosaic && window.IsVisible,
                "Esc cancels a mosaic move without cancelling the screenshot or changing its annotation");

            var copyMoveCommitted = false;
            void ObserveMove() => copyMoveCommitted = !editor.IsMovingMosaic && editor.Document.Items[0].Start == new PixelPoint(230, 85);
            window.CopyRequested += ObserveMove;
            editor.TryBeginMosaicMove(movePoint, monitor.Scale); editor.UpdateMosaicMove(new(movePoint.X + 80, movePoint.Y));
            ClickEditor(window, "CopyCaptureButton"); window.CopyRequested -= ObserveMove;
            Check(copyMoveCommitted, "Completing a capture commits an in-progress mosaic move before image synthesis");
            ClickEditor(window, "UndoButton");
            Check(editor.Document.Items[0] == originalMosaic, "Toolbar undo restores a committed mosaic move in one step");

            // Retain an actual WPF preview showing the final checker icon and selected dashed boundary.
            editor.TryBeginMosaicMove(movePoint, monitor.Scale); editor.EndMosaicMove();
            await Task.Delay(30);
            SaveAnnotationPreview(SaveVisual(window, "mosaic-selected.png"), selection, monitor, "mosaic-toolbar-preview.png");

            ClickEditor(window, "TextToolButton");
            editor.BeginStroke(new(selection.X + 30, selection.Y + 205), monitor.Scale);
            var textbox = (TextBox)window.FindName("AnnotationTextBox");
            textbox.Text = "短句";
            var shortWidth = textbox.Width;
            textbox.Text = "文字输入宽度会随着这一行内容动态增长，不再固定为原来的宽度";
            var longWidth = textbox.Width;
            Check(shortWidth < 120 && longWidth > 280 && longWidth > shortWidth,
                $"Inline text grows naturally beyond the old fixed width as more characters are entered: {shortWidth:F1} / {longWidth:F1}");
            textbox.Text = string.Concat(Enumerable.Repeat("到达选区边缘自动换行。", 8));
            await Task.Delay(40); textbox.UpdateLayout();
            Check(textbox.LineCount > 1 && Canvas.GetLeft(textbox) + textbox.ActualWidth <= (selection.Right - monitor.Bounds.X) / monitor.Scale + 1,
                "Long text wraps at the selection edge without extending outside the screenshot");
            textbox.Text = "这段文字可以再次编辑";
            Check(textbox.Width < longWidth, "Deleting characters shrinks the text input width again");
            InlineTextKey(window, Key.Enter);
            var original = editor.Document.Items[^1];
            Check(editor.Document.Items.Count == 2 && original.Tool == CaptureTool.Text && textbox.Visibility == Visibility.Collapsed,
                "Enter commits dynamically sized Chinese text as one annotation");

            var point = new PixelPoint(selection.X + original.Start.X + 3, selection.Y + original.Start.Y + 3);
            ClickEditor(window, "SelectToolButton");
            Check(editor.TryEditText(point, monitor.Scale) && textbox.IsVisible && textbox.Text == original.Text && editor.Tool == CaptureTool.Text,
                "Selecting existing text reopens its content and switches to the text editing tool");
            textbox.Text = "已经再次编辑 · 支持撤销";
            ((ComboBox)window.FindName("FontSizeBox")).SelectedIndex = 3;
            Descendants(window).OfType<Button>().Single(b => b.Tag as string == "#FF2488FF").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(30);
            textbox.UpdateLayout();
            Check(textbox.LineCount == 1, "Large Chinese text does not wrap prematurely while space remains before the selection edge");
            SaveAnnotationPreview(SaveVisual(window, "text-reediting.png"), selection, monitor, "text-reediting-preview.png");
            InlineTextKey(window, Key.Enter);
            var edited = editor.Document.Items[1];
            Check(editor.Document.Items.Count == 2 && edited.Start == original.Start && edited.Text.Contains("再次编辑")
                && edited.FontSize == 32 * monitor.Scale && edited.ColorArgb == 0xFF2488FF,
                "Reediting replaces the text in its original layer and preserves its anchor with the new style");
            ClickEditor(window, "UndoButton");
            Check(editor.Document.Items[1] == original, "Undo restores the previous text, width, color and font size");
            ClickEditor(window, "RedoButton");
            Check(editor.Document.Items[1] == edited, "Redo restores the edited text and its style");

            editor.TryEditText(point, monitor.Scale); textbox.Text = "放弃这次修改";
            InlineTextKey(window, Key.Escape);
            Check(editor.Document.Items[1] == edited && editor.EditingTextIndex is null && !textbox.IsVisible && window.IsVisible,
                "Esc abandons a reedit while keeping the previous text and capture session intact");
            editor.TryEditText(point, monitor.Scale); InlineTextKey(window, Key.Enter);
            ClickEditor(window, "UndoButton");
            Check(editor.Document.Items[1] == original, "Accepting an unchanged reedit does not consume an extra undo step");
            ClickEditor(window, "RedoButton");
            editor.TryEditText(point, monitor.Scale); textbox.Text = ""; InlineTextKey(window, Key.Enter);
            Check(editor.Document.Items.Count == 1, "Deleting all content while reediting removes the text annotation");
            ClickEditor(window, "UndoButton");
            Check(editor.Document.Items[1] == edited, "Undo restores text deleted through the inline editor");

            var copyObserved = false;
            window.CopyRequested += () => copyObserved = editor.EditingTextIndex is null && editor.Document.Items[1].Text == "复制前确认修改";
            editor.TryEditText(point, monitor.Scale); textbox.Text = "复制前确认修改";
            ClickEditor(window, "CopyCaptureButton");
            Check(copyObserved, "Copy toolbar commits pending text edits before requesting the shared image renderer");
            var result = AnnotationRenderer.Render(visualFrame, selection, editor.Document.Items);
            var exportPath = Path.Combine(_output, "mosaic-and-edited-text.png");
            CaptureFileWriter.SavePng(result, exportPath, Path.Combine(_output, "protected-cache"));
            Check(ScreenCaptureService.EncodePng(ScreenCaptureService.LoadImage(exportPath)).SequenceEqual(ScreenCaptureService.EncodePng(result)),
                "PNG saving preserves the mosaic and reedited text without including the inline editor");

            ClickEditor(window, "MosaicToolButton"); ((ComboBox)window.FindName("MosaicSizeBox")).SelectedIndex = 1;
            DrawEditorStroke(editor, new(selection.X + 620, selection.Y + 340), new(selection.Right + 100, selection.Bottom + 80));
            Check(editor.Document.Items[^1].End == new PixelPoint(selection.Width, selection.Height),
                "Dragging mosaic beyond the selection clamps its rectangle to the screenshot");
        }
        finally { window.Close(); }
        await TestMosaicReferenceStyleAsync(monitor, bounds);

        var negativeSession = new SelectionSession(frame.Bounds); negativeSession.SetSelection(frame.Bounds);
        using var scaled = new CaptureEditSession(negativeSession);
        scaled.SetTool(CaptureTool.Mosaic); scaled.MosaicSize = 16;
        DrawEditorStroke(scaled, new(-90, -60), new(-20, -10), 1.5);
        Check(scaled.Document.Items[0].MosaicBlockSize == 24 && AnnotationRenderer.Render(frame, frame.Bounds, scaled.Document.Items).PixelWidth == 93,
            "Mosaic particle sizes respect 150 percent scaling and negative physical coordinates");
        await TestMosaicCapturePipelineAsync(monitor);
        Check(_application.Windows.Count == 0, "Annotation feature checks close every capture, pin and tool window");
        Console.WriteLine($"{Checks.Count} mosaic and editable-text checks passed.");
    }

    private static void TestMosaicMovement()
    {
        var bounds = new PixelRect(-240, -160, 240, 160);
        var selection = new SelectionSession(bounds); selection.SetSelection(bounds);
        using var editor = new CaptureEditSession(selection);
        editor.SetTool(CaptureTool.Mosaic);
        DrawEditorStroke(editor, new(bounds.X + 20, bounds.Y + 20), new(bounds.X + 90, bounds.Y + 70), 1.5);
        var original = editor.Document.Items[0];
        var topLayer = new ScreenshotAnnotation(CaptureTool.Rectangle, new(190, 100), new(225, 140), 0xFFFF4D4F, 5);
        editor.Document.Add(topLayer);
        var anchor = new PixelPoint(bounds.X + 40, bounds.Y + 40);
        Check(editor.TryBeginMosaicMove(anchor, 1.5), "Mosaic dragging accepts negative desktop coordinates at 150 percent scaling");
        editor.UpdateMosaicMove(new(anchor.X + 5, anchor.Y + 12));
        editor.UpdateMosaicMove(new(anchor.X + 25, anchor.Y + 35));
        var moved = original with { Start = new(45, 55), End = new(115, 105) };
        Check(editor.MosaicMovePreview == moved && editor.Document.Items[0] == original,
            "Drag previews retain physical size and particle strength without mutating committed history");
        editor.EndMosaicMove();
        Check(editor.Document.Items.Count == 2 && editor.Document.Items[0] == moved && editor.Document.Items[1] == topLayer,
            "Releasing a mosaic drag replaces its original layer without reordering other annotations");
        var source = PixelPattern(240, 160);
        var frame = new DesktopFrame(source, bounds);
        var output = AnnotationRenderer.Render(frame, bounds, editor.Document.Items);
        Check(Pixel(output, 30, 30).SequenceEqual(Pixel(source, 30, 30)) && Pixel(output, 70, 70).SequenceEqual(Pixel(output, 71, 71)),
            "Moved mosaic exports restore the old region and pixelate the new region from the original screenshot");
        editor.Document.Undo();
        Check(editor.Document.Items[0] == original, "One undo restores the complete mosaic position before all intermediate drag updates");
        editor.Document.Redo();
        Check(editor.Document.Items[0] == moved, "Redo restores the moved mosaic with its original particle size");
        editor.SetTool(CaptureTool.Select);
        var movedAnchor = new PixelPoint(bounds.X + 65, bounds.Y + 75);
        Check(editor.TryBeginMosaicMove(movedAnchor, 1.5), "The select tool can drag an existing mosaic without moving the capture selection");
        editor.EndMosaicMove(); editor.Document.Undo();
        Check(editor.Document.Items[0] == original && selection.Selection == bounds,
            "Clicking a mosaic without moving it leaves undo history and the capture position unchanged");
        editor.Document.Redo();
        editor.TryBeginMosaicMove(movedAnchor, 1.5); editor.UpdateMosaicMove(new(movedAnchor.X + 10, movedAnchor.Y + 5)); editor.CancelMosaicMove();
        Check(!editor.IsMovingMosaic && editor.Document.Items[0] == moved && editor.SelectedMosaic == moved,
            "Cancelling a drag removes its preview and retains the selected original rectangle");
        editor.TryBeginMosaicMove(movedAnchor, 1.5); editor.UpdateMosaicMove(new(-1000, -1000));
        Check(editor.MosaicMovePreview is { Start: { X: 0, Y: 0 }, End: { X: 70, Y: 50 } },
            "Dragging beyond the top and left edges clamps the whole rectangle without shrinking it");
        editor.UpdateMosaicMove(new(1000, 1000));
        Check(editor.MosaicMovePreview is { Start: { X: 170, Y: 110 }, End: { X: 240, Y: 160 } },
            "Dragging beyond the right and bottom edges clamps the whole rectangle without shrinking it");
        editor.CancelMosaicMove();
        var overlap = moved with { Start = new(50, 60), End = new(110, 100), MosaicBlockSize = 8 };
        editor.Document.Add(overlap); editor.TryBeginMosaicMove(movedAnchor, 1.5);
        Check(editor.SelectedMosaicIndex == 2 && editor.SelectedMosaic == overlap,
            "Overlapping mosaic regions select the highest mosaic layer under the pointer");
        editor.CancelMosaicMove(); editor.Document.RemoveAt(2);
        var reversed = moved with { Start = moved.End, End = moved.Start };
        editor.Document.ReplaceAt(0, reversed); editor.TryBeginMosaicMove(movedAnchor, 1.5);
        editor.UpdateMosaicMove(new(movedAnchor.X + 10, movedAnchor.Y + 5)); editor.EndMosaicMove();
        Check(editor.Document.Items[0] == reversed with { Start = new(125, 110), End = new(55, 60) },
            "Reverse-drawn mosaics preserve endpoint orientation and rectangle dimensions when moved");
        editor.SetBusy(true);
        Check(!editor.TryBeginMosaicMove(movedAnchor, 1.5), "Busy captures reject mosaic moves");
        editor.SetBusy(false); editor.SetTool(CaptureTool.Pen);
        Check(!editor.TryBeginMosaicMove(movedAnchor, 1.5), "Other drawing tools keep their drawing behavior over existing mosaics");
        editor.SetTool(CaptureTool.Mosaic); editor.TryBeginMosaicMove(movedAnchor, 1.5); editor.Document.Reset();
        Check(!editor.IsMovingMosaic && editor.SelectedMosaic is null, "Document changes clear stale mosaic selection and drag previews");
        editor.Document.Add(original); editor.TryBeginMosaicMove(anchor, 1.5);
        selection.SetSelection(new(bounds.X, bounds.Y, 60, 40));
        Check(!editor.IsMovingMosaic && editor.SelectedMosaic is null, "Changing the capture selection cancels an outstanding mosaic move safely");
        editor.TryBeginMosaicMove(anchor, 1.5); editor.UpdateMosaicMove(new(-1000, -1000)); editor.EndMosaicMove();
        Check(PixelRect.Between(editor.Document.Items[0].Start, editor.Document.Items[0].End) is { Width: 70, Height: 50 }
            && AnnotationRenderer.Render(frame, selection.Selection, editor.Document.Items).PixelWidth == 60,
            "Mosaics clipped by a smaller capture remain movable with their size preserved and export clipped correctly");
    }

    private static void TestMosaicResizing()
    {
        var bounds = new PixelRect(-160, -110, 160, 110);
        var selection = new SelectionSession(bounds); selection.SetSelection(bounds);
        using var editor = new CaptureEditSession(selection);
        editor.SetTool(CaptureTool.Mosaic);
        PixelPoint Screen(int x, int y) => new(bounds.X + x, bounds.Y + y);
        DrawEditorStroke(editor, Screen(20, 20), Screen(100, 80), 1.5);
        var original = editor.Document.Items[0];
        void Select() { editor.TryBeginMosaicMove(Screen(55, 50), 1.5); editor.EndMosaicMove(); }
        Check(editor.HitTestMosaicHandle(Screen(20, 20), 1.5) == (SelectionAction.Left | SelectionAction.Top)
            && editor.HitTestMosaicHandle(Screen(100, 50), 1.5) == SelectionAction.Right
            && editor.HitTestMosaicHandle(Screen(55, 50), 1.5) == SelectionAction.None,
            "Corner and midpoint resize handles are distinct from dragging the mosaic interior");
        Check(editor.TryBeginMosaicResize(Screen(20, 20), 1.5), "Mosaic resize handles work with negative coordinates and 150 percent scaling");
        editor.UpdateMosaicMove(Screen(10, 15)); editor.UpdateMosaicMove(Screen(5, 10));
        var expanded = original with { Start = new(5, 10) };
        Check(editor.MosaicMovePreview == expanded && editor.Document.Items[0] == original,
            "Corner resizing keeps the opposite corner and particle strength fixed while previewing without history changes");
        editor.EndMosaicMove(); editor.Document.Undo();
        Check(editor.Document.Items[0] == original, "One undo restores the entire corner resize including all preview updates");
        editor.Document.Redo();
        Check(editor.Document.Items[0] == expanded, "Redo restores a resized mosaic's bounds and particle strength");
        editor.Document.Undo();
        var sideResults = new[]
        {
            (Anchor: Screen(20, 50), Target: Screen(35, 50), Expected: original with { Start = new(35, 20) }),
            (Anchor: Screen(100, 50), Target: Screen(125, 50), Expected: original with { End = new(125, 80) }),
            (Anchor: Screen(60, 20), Target: Screen(60, 30), Expected: original with { Start = new(20, 30) }),
            (Anchor: Screen(60, 80), Target: Screen(60, 95), Expected: original with { End = new(100, 95) })
        };
        foreach (var resize in sideResults)
        {
            Select();
            if (!editor.TryBeginMosaicResize(resize.Anchor, 1.5)) throw new InvalidOperationException("Midpoint handle could not begin resizing.");
            editor.UpdateMosaicMove(resize.Target); editor.EndMosaicMove();
            if (editor.Document.Items[0] != resize.Expected) throw new InvalidOperationException("Midpoint resizing changed an unrelated edge or style.");
            editor.Document.Undo();
        }
        Check(editor.Document.Items[0] == original, "All four midpoint handles resize only their corresponding edge");
        Select(); editor.TryBeginMosaicResize(Screen(100, 80), 1.5); editor.UpdateMosaicMove(Screen(1000, 1000));
        Check(editor.MosaicMovePreview is { Start: { X: 20, Y: 20 }, End: { X: 160, Y: 110 } },
            "Corner expansion stops at the capture's right and bottom edges");
        editor.UpdateMosaicMove(Screen(-1000, -1000));
        Check(editor.MosaicMovePreview is { Start: { X: 20, Y: 20 }, End: { X: 21, Y: 21 } },
            "Shrinking past the opposite corner retains a valid one-pixel minimum region");
        editor.CancelMosaicMove();
        Select(); editor.TryBeginMosaicResize(Screen(20, 20), 1.5); editor.UpdateMosaicMove(Screen(-1000, -1000));
        Check(editor.MosaicMovePreview is { Start: { X: 0, Y: 0 }, End: { X: 100, Y: 80 } },
            "Corner expansion stops at the capture's left and top edges");
        editor.CancelMosaicMove();
        Check(editor.Document.Items[0] == original && editor.MosaicAction == SelectionAction.None,
            "Cancelling a resize restores original coverage and clears the resize action");
        Select(); editor.TryBeginMosaicResize(Screen(100, 80), 1.5); editor.EndMosaicMove(); editor.Document.Undo();
        Check(editor.Document.Items.Count == 0, "Clicking a resize handle without changing bounds creates no additional undo step");
        editor.Document.Redo();
        var reversed = original with { Start = original.End, End = original.Start };
        editor.Document.ReplaceAt(0, reversed); Select();
        editor.TryBeginMosaicResize(Screen(100, 80), 1.5); editor.UpdateMosaicMove(Screen(120, 90)); editor.EndMosaicMove();
        Check(editor.Document.Items[0] == reversed with { Start = new(120, 90) },
            "Reverse-drawn mosaics resize the visual corner while preserving endpoint orientation");
        editor.Document.ReplaceAt(0, original); Select(); editor.TryBeginMosaicResize(Screen(20, 20), 1.5);
        editor.UpdateMosaicMove(Screen(50, 40)); editor.EndMosaicMove();
        var source = PixelPattern(160, 110);
        var image = AnnotationRenderer.Render(new(source, bounds), bounds, editor.Document.Items);
        Check(Pixel(image, 30, 30).SequenceEqual(Pixel(source, 30, 30)) && Pixel(image, 55, 55).SequenceEqual(Pixel(image, 56, 56)),
            "Shrinking mosaic restores uncovered pixels and keeps the resized region pixelated in exports");
        editor.SetTool(CaptureTool.Select); Select(); editor.TryBeginMosaicResize(Screen(100, 80), 1.5);
        editor.UpdateMosaicMove(Screen(115, 95)); editor.EndMosaicMove();
        Check(selection.Selection == bounds && editor.Document.Items[0].End == new PixelPoint(115, 95),
            "The select tool can resize mosaic handles while keeping the screenshot selection fixed");
    }

    private static async Task TestMosaicReferenceStyleAsync(MonitorDescriptor monitor, PixelRect bounds)
    {
        var selection = new PixelRect(monitor.Bounds.X + 350, monitor.Bounds.Y + 210, 620, 340);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 31, 33)), null, new Rect(0, 0, bounds.Width, bounds.Height));
            var labels = new[] { "public void CaptureRegion()", "    var account = \"demo@example.com\";", "    var token = \"example-value\";", "    ApplyMosaic(selection);", "    SaveImage(result);" };
            for (var i = 0; i < labels.Length; i++)
                context.DrawText(new FormattedText(labels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 19, new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(83, 185, 239) : Color.FromRgb(163, 203, 130)), 1),
                    new(selection.X - bounds.X + 25, selection.Y - bounds.Y + 32 + i * 55));
        }
        var source = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
        source.Render(drawing); source.Freeze();
        var session = new SelectionSession(bounds); session.SetSelection(selection);
        using var editor = new CaptureEditSession(session); editor.SetTool(CaptureTool.Mosaic);
        var window = new CaptureWindow(new(source, bounds), monitor, session, editor) { ShowActivated = false, IsHitTestVisible = false };
        DesktopEnvironment.DoNotActivate(window); DesktopEnvironment.MakeClickThrough(window); window.Show();
        try
        {
            DrawEditorStroke(editor, new(selection.X + 110, selection.Y + 70), new(selection.X + 450, selection.Y + 250));
            await Task.Delay(40);
            var visual = SaveVisual(window, "mosaic-dark-editor.png");
            var mosaicBounds = new Int32Rect(selection.X - monitor.Bounds.X + 98, selection.Y - monitor.Bounds.Y + 58, 364, 204);
            File.WriteAllBytes(Path.Combine(_output, "mosaic-reference-style.png"), ScreenCaptureService.EncodePng(new CroppedBitmap(visual, mosaicBounds)));
            var white = Enumerable.Range(125, 30).Select(x => Pixel(visual, selection.X - monitor.Bounds.X + x, selection.Y - monitor.Bounds.Y + 70)).ToArray();
            Check(white.Any(p => p.Take(3).All(v => v >= 248)) && white.Any(p => p.Take(3).All(v => v < 70))
                && !Enumerable.Range(125, 30).Any(x => Pixel(visual, selection.X - monitor.Bounds.X + x, selection.Y - monitor.Bounds.Y + 69).Take(3).All(v => v >= 248)),
                "Reference-style mosaic uses short single-pixel white dashes with clear gaps on a dark background");
        }
        finally { window.Close(); }
    }

    private static BitmapSource AnnotationSample(PixelRect bounds, PixelRect selection)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 238, 250)), null, new Rect(0, 0, bounds.Width, bounds.Height));
            var rect = new Rect(selection.X - bounds.X, selection.Y - bounds.Y, selection.Width, selection.Height);
            context.DrawRectangle(Brushes.White, null, rect);
            void Label(string value, int x, int y, double size, Brush brush) => context.DrawText(new FormattedText(value,
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), size, brush, 1), new(rect.X + x, rect.Y + y));
            Label("演示信息 · 框选隐藏敏感区域", 30, 24, 23, new SolidColorBrush(Color.FromRgb(23, 43, 77)));
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(239, 246, 255)), null, new Rect(rect.X + 28, rect.Y + 72, 650, 102), 10, 10);
            Label("电话：138 0012 3456    账号：demo@example.com", 46, 100, 20, Brushes.Black);
            Label("选择马赛克工具后，拖动框选需要处理的范围。", 30, 305, 16, new SolidColorBrush(Color.FromRgb(98, 114, 141)));
        }
        var image = new RenderTargetBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing); image.Freeze(); return image;
    }

    private static void SaveAnnotationPreview(BitmapSource visual, PixelRect selection, MonitorDescriptor monitor, string name)
    {
        var crop = new CroppedBitmap(visual, new Int32Rect(selection.X - monitor.Bounds.X - 20,
            selection.Y - monitor.Bounds.Y - 42, selection.Width + 40, selection.Height + 185));
        File.WriteAllBytes(Path.Combine(_output, name), ScreenCaptureService.EncodePng(crop));
    }

    private static async Task TestMosaicCapturePipelineAsync(MonitorDescriptor monitor)
    {
        var foreground = GetForegroundWindow();
        var root = Path.Combine(_output, "pipeline-cache");
        var repository = new ScreenshotRepository(root); repository.Load(new(), DateTimeOffset.UtcNow); repository.Clear();
        new SettingsStore(root).Save(new() { AutoCopy = false, AutoExpand = false, SuppressInFullscreen = false,
            PinHotkey = TestShortcut(0x7A),
            Hotkey = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x79, KeyName = "F10" } });
        var destination = Path.Combine(_output, "pipeline-saved.png");
        using var controller = new AppController(root, _ => destination);
        await controller.StartAsync(showInitialDock: false);
        var fixture = new Window { Title = "ScreenshotDock mosaic fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowActivated = false, Width = 620, Height = 330,
            Content = new Image { Source = PixelPattern(620, 330), Stretch = Stretch.Fill } };
        var fixtureBounds = new PixelRect(monitor.Bounds.X + 300, monitor.Bounds.Y + 370, 620, 330);
        DesktopEnvironment.DoNotActivate(fixture); fixture.Show(); DesktopEnvironment.Place(fixture, fixtureBounds);
        await Task.Delay(60);
        var selection = new PixelRect(fixtureBounds.X + 20, fixtureBounds.Y + 20, 540, 270);
        try
        {
            foreach (var action in new[] { "SaveCaptureButton", "PinCaptureButton" })
            {
                var capture = controller.CaptureAsync();
                for (var i = 0; i < 100 && !_application.Windows.OfType<CaptureWindow>().Any(); i++) await Task.Delay(10);
                var overlays = _application.Windows.OfType<CaptureWindow>().ToArray();
                foreach (var overlay in overlays) { DesktopEnvironment.DoNotActivate(overlay); DesktopEnvironment.MakeClickThrough(overlay); overlay.IsHitTestVisible = false; }
                var window = overlays.First(w => w.PointToScreen(new Point()).X == monitor.Bounds.X);
                window.Editor.Selection.SetSelection(selection);
                ClickEditor(window, "MosaicToolButton");
                DrawEditorStroke(window.Editor, new(selection.X + 30, selection.Y + 30), new(selection.X + 250, selection.Y + 110));
                ClickEditor(window, "TextToolButton"); window.Editor.BeginStroke(new(selection.X + 35, selection.Y + 150), monitor.Scale);
                ((TextBox)window.FindName("AnnotationTextBox")).Text = "首次标注"; window.CommitText();
                var text = window.Editor.Document.Items[1];
                window.Editor.TryEditText(new(selection.X + text.Start.X + 2, selection.Y + text.Start.Y + 2), monitor.Scale);
                ((TextBox)window.FindName("AnnotationTextBox")).Text = action == "SaveCaptureButton" ? "修改后的保存文字" : "修改后的贴图文字";
                ClickEditor(window, action);
                await capture.WaitAsync(TimeSpan.FromSeconds(5));
                repository.Load(new(), DateTimeOffset.UtcNow);
                var retained = ScreenCaptureService.LoadImage(repository.GetImagePath(repository.Items[0].Id));
                Check(retained.PixelWidth == 540 && retained.PixelHeight == 270 && Pixel(retained, 50, 50).SequenceEqual(Pixel(retained, 59, 59)),
                    "Full capture pipeline preserves physical dimensions and mosaic blocks: " + action);
                Check(RedAtSomeText(retained), "Full capture pipeline commits pending Chinese reedited text: " + action);
                if (action == "SaveCaptureButton")
                    Check(File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(repository.GetImagePath(repository.Items[0].Id))),
                        "Saved file and retained screenshot contain identical mosaic and edited-text pixels");
                else
                {
                    var pin = _application.Windows.OfType<PinnedImageWindow>().Single();
                    Check(ScreenCaptureService.EncodePng(pin.Image).SequenceEqual(ScreenCaptureService.EncodePng(retained)),
                        "Pinned image uses exactly the same flattened mosaic and text as the cache");
                }
            }
        }
        finally { fixture.Close(); controller.Dispose(); SetForegroundWindow(foreground); }
    }

    private static bool RedAtSomeText(BitmapSource image)
    {
        var rect = new Int32Rect(35, 150, 320, 60);
        var bytes = new byte[rect.Width * rect.Height * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(rect, bytes, rect.Width * 4, 0);
        return Enumerable.Range(0, bytes.Length / 4).Count(i => bytes[i * 4 + 2] == 255 && bytes[i * 4 + 1] == 77 && bytes[i * 4] == 79) > 50;
    }
}
