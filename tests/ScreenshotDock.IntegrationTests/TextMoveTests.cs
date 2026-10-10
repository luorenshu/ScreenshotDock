using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.App.Capture;
using ScreenshotDock.App.Views;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Capture;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.IntegrationTests;

internal static partial class Program
{
    private static Thumb StartTextDrag(CaptureWindow window, string edge = "Top")
    {
        var thumb = (Thumb)window.FindName("TextMove" + edge);
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        return thumb;
    }

    private static void MoveTextDrag(Thumb thumb, double x, double y)
    {
        thumb.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.UpdateLayout();
    }

    private static void EndTextDrag(Thumb thumb, bool cancel = false) =>
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, cancel) { RoutedEvent = Thumb.DragCompletedEvent });

    private static async Task RunTextMoveChecksAsync()
    {
        var monitor = DesktopEnvironment.PrimaryMonitor;
        var desktop = DesktopEnvironment.VirtualBounds;
        var selection = new PixelRect(monitor.Bounds.X + 280, monitor.Bounds.Y + 190, 680, 380);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawImage(EditorFixture(desktop.Width, desktop.Height), new Rect(0, 0, desktop.Width, desktop.Height));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(34, 39, 48)), null,
                new Rect(selection.X - desktop.X, selection.Y - desktop.Y + 190, selection.Width, 190));
        }
        var source = new RenderTargetBitmap(desktop.Width, desktop.Height, 96, 96, PixelFormats.Pbgra32);
        source.Render(drawing); source.Freeze();
        var frame = new DesktopFrame(source, desktop);
        var session = new SelectionSession(desktop); session.SetSelection(selection);
        using var editor = new CaptureEditSession(session);
        var window = new CaptureWindow(frame, monitor, session, editor) { ShowActivated = false };
        DesktopEnvironment.DoNotActivate(window); DesktopEnvironment.MakeClickThrough(window); window.Show();
        PixelPoint Screen(int x, int y) => new(selection.X + x, selection.Y + y);
        var textbox = (TextBox)window.FindName("AnnotationTextBox");
        var host = (FrameworkElement)window.FindName("TextEditorHost");
        Point Position() => new(Canvas.GetLeft(host), Canvas.GetTop(host));
        DependencyObject? Hit(Point point)
        {
            var hit = window.InputHitTest(host.TranslatePoint(point, window)) as DependencyObject;
            while (hit is not null && hit is not Thumb && hit is not TextBox) hit = VisualTreeHelper.GetParent(hit);
            return hit;
        }
        bool BackgroundMatches(BitmapSource visual)
        {
            var point = host.PointToScreen(new Point(textbox.ActualWidth - 10, 18));
            return Pixel(visual, (int)Math.Round(point.X - monitor.Bounds.X), (int)Math.Round(point.Y - monitor.Bounds.Y))
                .SequenceEqual(Pixel(source, (int)Math.Round(point.X - desktop.X), (int)Math.Round(point.Y - desktop.Y)));
        }
        try
        {
            editor.SetTool(CaptureTool.Rectangle); DrawEditorStroke(editor, Screen(350, 40), Screen(530, 140), monitor.Scale);
            var rectangle = editor.Document.Items[0];
            editor.SetTool(CaptureTool.Text); editor.BeginStroke(Screen(40, 45), monitor.Scale);
            textbox.Text = "可拖动的文字\n输入背景透明";
            await Task.Delay(30); window.UpdateLayout();
            var initial = Position();
            var size = new Size(textbox.ActualWidth, textbox.ActualHeight);
            Check(Hit(new(size.Width / 2, 2)) is Thumb && Hit(new(2, size.Height / 2)) is Thumb
                && Hit(new(size.Width - 2, size.Height / 2)) is Thumb && Hit(new(size.Width / 2, size.Height - 2)) is Thumb
                && Hit(new(12, 15)) is TextBox,
                "All four text editor edges move the box while the interior remains available for normal text input");
            var light = SaveVisual(window, "text-transparent-light.png");
            SaveAnnotationPreview(light, selection, monitor, "text-transparent-light-preview.png");
            Check(BackgroundMatches(light), "The active text editor reveals unchanged screenshot pixels on a light background");

            textbox.Select(2, 4);
            var thumb = StartTextDrag(window);
            MoveTextDrag(thumb, 10, 4); MoveTextDrag(thumb, 25, 14); EndTextDrag(thumb);
            window.UpdateLayout();
            Check(Position() == initial + new Vector(35, 18) && textbox.SelectionStart == 2 && textbox.SelectionLength == 4
                && textbox.Text == "可拖动的文字\n输入背景透明" && size == new Size(textbox.ActualWidth, textbox.ActualHeight),
                "Dragging text accumulates movement without changing content, selection, wrapping or size");
            Check(editor.Document.Items.Count == 1 && editor.Document.Items[0] == rectangle && session.Selection == selection,
                "Moving pending text does not commit history or move the screenshot selection");
            var movedPosition = Position();
            foreach (var edge in new[] { "Top", "Bottom", "Left", "Right" })
            {
                thumb = StartTextDrag(window, edge); MoveTextDrag(thumb, 9, 7); EndTextDrag(thumb, true);
                Check(Position() == movedPosition && textbox.IsVisible,
                    "A cancelled drag on the " + edge + " text edge restores its position and leaves input open");
            }
            thumb = StartTextDrag(window); MoveTextDrag(thumb, -10000, -10000); window.UpdateLayout();
            var origin = host.PointToScreen(new Point());
            Check(origin.X >= selection.X && origin.Y >= selection.Y,
                "Text movement stops at the top and left screenshot boundaries");
            MoveTextDrag(thumb, 20000, 20000); window.UpdateLayout();
            var end = host.PointToScreen(new Point(textbox.ActualWidth, textbox.ActualHeight));
            Check(end.X <= selection.Right + 1 && end.Y <= selection.Bottom + 1,
                "The complete text editor stays inside the bottom and right screenshot boundaries");
            var edgePosition = Position();
            MoveTextDrag(thumb, 4000, 4000); MoveTextDrag(thumb, -10, -10);
            Check(Position() == edgePosition - new Vector(10, 10),
                "Text follows the pointer back inside after repeated movement beyond the screenshot boundary");
            EndTextDrag(thumb, true);
            thumb = StartTextDrag(window); MoveTextDrag(thumb, 40, 25); InlineTextKey(window, Key.Escape);
            Check(Position() == movedPosition && textbox.IsVisible && editor.Document.Items.Count == 1,
                "Esc during a text drag cancels only the movement and preserves the pending input");

            textbox.Select(textbox.Text.Length, 0);
            thumb = StartTextDrag(window, "Left"); MoveTextDrag(thumb, 20, 150); EndTextDrag(thumb);
            await Task.Delay(30); window.UpdateLayout();
            var dark = SaveVisual(window, "text-transparent-dark.png");
            SaveAnnotationPreview(dark, selection, monitor, "text-transparent-dark-preview.png");
            Check(BackgroundMatches(dark), "Moving the active editor onto a dark background keeps it fully transparent");
            var finalPosition = host.PointToScreen(new Point(6, 6));
            var expectedAnchor = new PixelPoint((int)Math.Round(finalPosition.X - selection.X), (int)Math.Round(finalPosition.Y - selection.Y));
            InlineTextKey(window, Key.Enter);
            var original = editor.Document.Items[1];
            Check(original.Start == expectedAnchor && original.End == expectedAnchor && editor.Document.Items[0] == rectangle
                && !textbox.IsVisible, "Enter commits the text at its dragged position without changing other layers");
            var exported = AnnotationRenderer.Render(frame, selection, editor.Document.Items);
            File.WriteAllBytes(Path.Combine(_output, "text-moved-export.png"), ScreenCaptureService.EncodePng(exported));
            Check(Pixel(exported, 50, 65).SequenceEqual(Pixel(ScreenCaptureService.Crop(frame, selection), 50, 65))
                && Enumerable.Range(original.Start.Y, 30).Any(y => Enumerable.Range(original.Start.X, 120).Any(x => RedAt(exported, x, y))),
                "Export restores the old text area and draws actual text pixels at the moved location");

            PixelPoint TextPoint(ScreenshotAnnotation annotation) => Screen(annotation.Start.X + 3, annotation.Start.Y + 3);
            Check(editor.TryEditText(TextPoint(original), monitor.Scale) && textbox.Text == original.Text,
                "Text moved during entry can be selected again for editing");
            window.UpdateLayout();
            Check(Math.Abs(textbox.Width - (original.TextWidth / monitor.Scale + 12)) < 0.01,
                "Reopening text retains its committed line wrapping before movement");
            thumb = StartTextDrag(window, "Right"); MoveTextDrag(thumb, -25, -30); EndTextDrag(thumb); InlineTextKey(window, Key.Enter);
            var moved = original with { Start = new(original.Start.X - 25, original.Start.Y - 30), End = new(original.End.X - 25, original.End.Y - 30) };
            Check(editor.Document.Items.Count == 2 && editor.Document.Items[1] == moved,
                "Moving existing text with no content change updates its anchor and preserves its full original style and width");
            editor.Document.Undo();
            Check(editor.Document.Items[1] == original, "One undo restores the complete text movement");
            editor.Document.Redo();
            Check(editor.Document.Items[1] == moved, "Redo restores the moved text with its original style");
            editor.TryEditText(TextPoint(moved), monitor.Scale); thumb = StartTextDrag(window);
            MoveTextDrag(thumb, 15, 10); EndTextDrag(thumb); textbox.Text = "这次位置和内容都放弃"; InlineTextKey(window, Key.Escape);
            Check(editor.Document.Items[1] == moved && !textbox.IsVisible && editor.EditingTextIndex is null,
                "Esc after repositioning a reedit abandons both uncommitted content and position changes");
            editor.TryEditText(TextPoint(moved), monitor.Scale); var beforeBusy = Position(); editor.SetBusy(true);
            thumb = StartTextDrag(window); MoveTextDrag(thumb, 50, 40); EndTextDrag(thumb);
            Check(Position() == beforeBusy && !host.IsEnabled, "Busy capture operations disable text repositioning");
            editor.SetBusy(false); InlineTextKey(window, Key.Enter); editor.Document.Undo();
            Check(editor.Document.Items[1] == original, "Confirming an unchanged text editor adds no movement undo step");
            editor.Document.Redo();

            editor.TryEditText(TextPoint(moved), monitor.Scale); thumb = StartTextDrag(window);
            MoveTextDrag(thumb, 10, 5); EndTextDrag(thumb); textbox.Text = "移动后确认并保存";
            var committedForSave = false;
            window.SaveRequested += () => committedForSave = editor.EditingTextIndex is null && !textbox.IsVisible
                && editor.Document.Items[1].Start == new PixelPoint(moved.Start.X + 10, moved.Start.Y + 5)
                && editor.Document.Items[1].Text == "移动后确认并保存";
            ClickEditor(window, "SaveCaptureButton");
            Check(committedForSave, "Saving confirms both pending text content and its latest dragged position before synthesis");
        }
        finally { window.Close(); }
    }
}
