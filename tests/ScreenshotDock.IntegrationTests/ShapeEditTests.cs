using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static async Task RunShapeEditChecksAsync()
    {
        foreach (var tool in new[] { CaptureTool.Rectangle, CaptureTool.Ellipse }) TestShapeEditSession(tool);
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var bounds = DesktopEnvironment.VirtualBounds;
        var selection = new PixelRect(monitor.Bounds.X + 320, monitor.Bounds.Y + 200, 680, 380);
        var frame = new DesktopFrame(EditorFixture(bounds.Width, bounds.Height), bounds);
        var session = new SelectionSession(bounds); session.SetSelection(selection);
        using var editor = new CaptureEditSession(session);
        var window = new CaptureWindow(frame, monitor, session, editor) { ShowActivated = false, IsHitTestVisible = false };
        DesktopEnvironment.DoNotActivate(window); DesktopEnvironment.MakeClickThrough(window); window.Show();
        PixelPoint Screen(int x, int y) => new(selection.X + x, selection.Y + y);
        try
        {
            ClickEditor(window, "RectangleToolButton"); DrawEditorStroke(editor, Screen(40, 50), Screen(250, 160));
            Check(editor.SelectedRegionIndex == 0 && editor.SelectedRegion?.Tool == CaptureTool.Rectangle,
                "The rectangle toolbar tool selects a newly drawn shape for immediate adjustment");
            await Task.Delay(30);
            SaveAnnotationPreview(SaveVisual(window, "rectangle-selected.png"), selection, monitor, "rectangle-selected-preview.png");
            var original = editor.Document.Items[0];
            editor.TryBeginRegionMove(Screen(130, 110), monitor.Scale); editor.UpdateRegionEdit(Screen(175, 135));
            await Task.Delay(30);
            var preview = SaveVisual(window, "rectangle-moving.png");
            var exported = AnnotationRenderer.Render(frame, selection, [editor.RegionPreview!]);
            Check(Pixel(preview, selection.X - monitor.Bounds.X + 83, selection.Y - monitor.Bounds.Y + 110)
                    .SequenceEqual(Pixel(exported, 83, 110)) && RedAt(exported, 83, 110) && !RedAt(exported, 42, 90),
                "Moving a rectangle previews the exported outline at its new position and restores its old pixels");
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.KeyDownEvent });
            Check(window.IsVisible && !editor.IsEditingRegion && editor.Document.Items[0] == original,
                "Esc cancels the shape adjustment while keeping the screenshot open");

            editor.TryBeginRegionResize(Screen(250, 160), monitor.Scale); editor.UpdateRegionEdit(Screen(285, 190));
            var committedBeforeCopy = false;
            window.CopyRequested += () => committedBeforeCopy = !editor.IsEditingRegion && editor.Document.Items[0].End == new PixelPoint(285, 190);
            ClickEditor(window, "CopyCaptureButton");
            Check(committedBeforeCopy, "The copy action commits an active shape resize before requesting image synthesis");
            ClickEditor(window, "UndoButton");
            Check(editor.Document.Items[0] == original, "The toolbar undoes a complete shape resize in one step");

            ClickEditor(window, "EllipseToolButton"); DrawEditorStroke(editor, Screen(350, 120), Screen(510, 280));
            Check(editor.SelectedRegionIndex == 1 && editor.SelectedRegion?.Tool == CaptureTool.Ellipse,
                "The ellipse toolbar tool selects a newly drawn circle for immediate adjustment");
            await Task.Delay(30);
            SaveAnnotationPreview(SaveVisual(window, "ellipse-selected.png"), selection, monitor, "ellipse-selected-preview.png");
            editor.TryBeginRegionResize(Screen(510, 200), monitor.Scale); editor.UpdateRegionEdit(Screen(560, 200));
            await Task.Delay(30);
            SaveAnnotationPreview(SaveVisual(window, "ellipse-resizing.png"), selection, monitor, "ellipse-resizing-preview.png");
            var shape = editor.RegionPreview!;
            exported = AnnotationRenderer.Render(frame, selection, [original, shape]);
            Check(RedAt(exported, 560, 200) && !RedAt(exported, 510, 200),
                "Resizing a circle updates its ellipse outline rather than scaling a cached bitmap");
            var canvas = ((Panel)window.FindName("Surface")).Children.OfType<FrameworkElement>().First();
            canvas.RaiseEvent(new MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent });
            Check(!editor.IsEditingRegion && editor.Document.Items[1].End == new PixelPoint(510, 280),
                "Losing pointer capture discards an unfinished shape resize");
            ClickEditor(window, "SelectToolButton");
            editor.TryBeginRegionMove(Screen(430, 200), monitor.Scale); editor.EndRegionEdit();
            Check(editor.SelectedRegionIndex == 1 && session.Selection == selection,
                "The selection toolbar tool selects an existing ellipse without moving the screenshot area");
            editor.SetTool(CaptureTool.Eraser); editor.BeginStroke(Screen(350, 200), monitor.Scale);
            Check(editor.Document.Items.Count == 1 && editor.SelectedRegion is null,
                "Removing an editable ellipse clears its editing handles and leaves the rectangle intact");
        }
        finally { window.Close(); }
    }

    private static void TestShapeEditSession(CaptureTool tool)
    {
        var bounds = new PixelRect(-800, -300, 320, 220);
        var selection = new SelectionSession(bounds); selection.SetSelection(bounds);
        using var editor = new CaptureEditSession(selection);
        PixelPoint Screen(int x, int y) => new(bounds.X + x, bounds.Y + y);
        editor.SetTool(tool); DrawEditorStroke(editor, Screen(30, 40), Screen(170, 130), 1.5);
        var original = editor.Document.Items[0];
        var label = tool.ToString() + ": ";
        Check(editor.SelectedRegion == original && original.Thickness == 7.5, label + "drawing selects the shape and preserves physical stroke width");
        Check(editor.HitTestRegion(Screen(100, 85), 1.5) == 0 && !AnnotationRenderer.HitTest(original, new(100, 85), 0),
            label + "the hollow interior selects for moving without becoming an eraser target");
        if (tool == CaptureTool.Ellipse)
            Check(editor.HitTestRegion(Screen(31, 41), 1.5) is null, label + "the empty corners outside the ellipse do not swallow clicks");
        Check(editor.TryBeginRegionMove(Screen(100, 85), 1.5), label + "moving accepts negative desktop coordinates at 150 percent scaling");
        editor.UpdateRegionEdit(Screen(110, 90)); editor.UpdateRegionEdit(Screen(125, 100));
        var moved = original with { Start = new(55, 55), End = new(195, 145) };
        Check(editor.RegionPreview == moved && editor.Document.Items[0] == original, label + "intermediate movement remains a preview without changing history or style");
        editor.EndRegionEdit(); editor.Document.Undo();
        Check(editor.Document.Items[0] == original, label + "one undo restores the complete movement");
        editor.Document.Redo();
        Check(editor.Document.Items[0] == moved, label + "redo restores the adjusted position");
        editor.SetTool(CaptureTool.Select); editor.TryBeginRegionMove(Screen(125, 100), 1.5); editor.EndRegionEdit(); editor.Document.Undo();
        Check(editor.Document.Items[0] == original && selection.Selection == bounds, label + "clicking with no movement adds no undo step and preserves the screenshot");

        var handles = new[]
        {
            (new PixelPoint(30, 40), new PixelPoint(20, 30), new PixelPoint(20, 30), new PixelPoint(170, 130)),
            (new PixelPoint(170, 40), new PixelPoint(190, 30), new PixelPoint(30, 30), new PixelPoint(190, 130)),
            (new PixelPoint(30, 130), new PixelPoint(20, 150), new PixelPoint(20, 40), new PixelPoint(170, 150)),
            (new PixelPoint(170, 130), new PixelPoint(190, 150), new PixelPoint(30, 40), new PixelPoint(190, 150)),
            (new PixelPoint(100, 40), new PixelPoint(100, 30), new PixelPoint(30, 30), new PixelPoint(170, 130)),
            (new PixelPoint(100, 130), new PixelPoint(100, 150), new PixelPoint(30, 40), new PixelPoint(170, 150)),
            (new PixelPoint(30, 85), new PixelPoint(20, 85), new PixelPoint(20, 40), new PixelPoint(170, 130)),
            (new PixelPoint(170, 85), new PixelPoint(190, 85), new PixelPoint(30, 40), new PixelPoint(190, 130))
        };
        editor.TryBeginRegionMove(Screen(100, 85), 1.5); editor.EndRegionEdit();
        if (tool == CaptureTool.Ellipse)
            Check(handles.Take(4).All(handle =>
                editor.HitTestRegionHandle(Screen(handle.Item1.X, handle.Item1.Y), 1.5) == SelectionAction.None
                && !editor.TryBeginRegionResize(Screen(handle.Item1.X, handle.Item1.Y), 1.5)),
                label + "the four hidden corner handles cannot begin resizing");
        for (var i = tool == CaptureTool.Ellipse ? 4 : 0; i < handles.Length; i++)
        {
            var (anchor, target, start, end) = handles[i];
            editor.TryBeginRegionMove(Screen(100, 85), 1.5); editor.EndRegionEdit();
            Check(editor.TryBeginRegionResize(Screen(anchor.X, anchor.Y), 1.5), label + "resize handle " + i + " begins adjustment");
            editor.UpdateRegionEdit(Screen(target.X, target.Y)); editor.EndRegionEdit();
            Check(editor.Document.Items[0] == original with { Start = start, End = end }, label + "resize handle " + i + " keeps unrelated edges and style unchanged");
            editor.Document.Undo();
        }
        editor.TryBeginRegionMove(Screen(100, 85), 1.5); editor.EndRegionEdit();
        var resizeAnchor = tool == CaptureTool.Ellipse ? Screen(170, 85) : Screen(170, 130);
        editor.TryBeginRegionResize(resizeAnchor, 1.5); editor.UpdateRegionEdit(Screen(1000, 1000));
        Check(editor.RegionPreview?.End == new PixelPoint(320, tool == CaptureTool.Ellipse ? 130 : 220), label + "enlargement stops at the screenshot boundary");
        editor.UpdateRegionEdit(Screen(-1000, -1000));
        Check(editor.RegionPreview?.End == new PixelPoint(31, tool == CaptureTool.Ellipse ? 130 : 41), label + "shrinking across the opposite edge preserves a valid minimum");
        editor.CancelRegionEdit();
        Check(editor.Document.Items[0] == original && !editor.IsEditingRegion, label + "cancelling restores the original shape");
        var reversed = original with { Start = original.End, End = original.Start };
        editor.Document.ReplaceAt(0, reversed); editor.TryBeginRegionMove(Screen(100, 85), 1.5); editor.EndRegionEdit();
        editor.TryBeginRegionResize(resizeAnchor, 1.5); editor.UpdateRegionEdit(Screen(190, 145)); editor.EndRegionEdit();
        Check(editor.Document.Items[0] == reversed with { Start = new(190, tool == CaptureTool.Ellipse ? 130 : 145) }, label + "reverse-drawn shapes resize the visible handle correctly");
        var overlap = original with { Start = new(60, 60), End = new(150, 120), ColorArgb = 0xFF2488FF };
        editor.Document.Add(overlap); editor.TryBeginRegionMove(Screen(100, 85), 1.5);
        Check(editor.SelectedRegionIndex == 1 && editor.RegionPreview == overlap, label + "overlapping shapes select the highest eligible layer");
        editor.CancelRegionEdit(); editor.SetBusy(true);
        Check(!editor.TryBeginRegionMove(Screen(100, 85), 1.5) && !editor.TryBeginRegionResize(Screen(150, 120), 1.5), label + "busy captures reject adjustments");
        editor.SetBusy(false); editor.SetTool(CaptureTool.Pen);
        Check(!editor.TryBeginRegionMove(Screen(100, 85), 1.5), label + "other drawing tools retain their drawing behavior");
        editor.SetTool(tool); editor.TryBeginRegionMove(Screen(100, 85), 1.5); editor.UpdateRegionEdit(Screen(110, 90));
        editor.Document.Undo();
        Check(!editor.IsEditingRegion && editor.SelectedRegion is null, label + "history changes clear stale selected layers and previews");
    }
}
