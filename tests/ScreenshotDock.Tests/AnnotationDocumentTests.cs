using System.Collections.Immutable;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using Xunit;

namespace ScreenshotDock.Tests;

public sealed class AnnotationDocumentTests
{
    private static ScreenshotAnnotation Box(int x = 10) => new(CaptureTool.Rectangle, new(x, 10), new(x + 60, 50), 0xFFFF4D4F, 3);

    [Fact]
    public void EditingTextKeepsLayerOrderAndUndoRestoresItsTextAndStyle()
    {
        var document = new AnnotationDocument();
        var original = new ScreenshotAnnotation(CaptureTool.Text, new(12, 15), new(12, 15), 0xFFFF4D4F, 5, 20, "原文字", TextWidth: 160);
        var later = Box(100);
        document.Add(original); document.Add(later);
        var edited = original with { Text = "修改后的文字", FontSize = 32, ColorArgb = 0xFF2488FF, TextWidth = 360 };
        Assert.True(document.ReplaceAt(0, edited));
        Assert.Equal([edited, later], document.Items);
        document.Undo(); Assert.Equal([original, later], document.Items);
        document.Redo(); Assert.Equal([edited, later], document.Items);
    }

    [Fact]
    public void UnchangedOrInvalidTextEditsDoNotAddHistoryOrDiscardRedo()
    {
        var document = new AnnotationDocument();
        var text = new ScreenshotAnnotation(CaptureTool.Text, new(5, 5), new(5, 5), 0xFFFF4D4F, 5, Text: "原文字");
        document.Add(text); document.Add(Box()); document.Undo();
        Assert.True(document.ReplaceAt(0, text));
        Assert.False(document.ReplaceAt(0, text with { Text = " " }));
        Assert.False(document.ReplaceAt(1, text));
        Assert.True(document.CanRedo);
        Assert.True(document.Undo()); Assert.Empty(document.Items);
    }

    [Fact]
    public void TextReplacementAfterUndoDiscardsTheAbandonedEdit()
    {
        var document = new AnnotationDocument();
        var text = new ScreenshotAnnotation(CaptureTool.Text, new(5, 5), new(5, 5), 0xFFFF4D4F, 5, Text: "原文字");
        document.Add(text); document.ReplaceAt(0, text with { Text = "第一次修改" }); document.Undo();
        document.ReplaceAt(0, text with { Text = "重新修改" });
        Assert.False(document.Redo()); Assert.Equal("重新修改", document.Items[0].Text);
    }

    [Fact]
    public void MosaicRejectsEmptyRegionsAndInvalidBlockSizesAndPreservesUndo()
    {
        var document = new AnnotationDocument();
        var mosaic = new ScreenshotAnnotation(CaptureTool.Mosaic, new(80, 60), new(10, 20), 0, 5, MosaicBlockSize: 24);
        Assert.True(document.Add(mosaic));
        Assert.False(document.Add(mosaic with { End = mosaic.Start }));
        Assert.False(document.Add(mosaic with { MosaicBlockSize = 1 }));
        Assert.False(document.Add(mosaic with { MosaicBlockSize = 513 }));
        document.Undo(); Assert.Empty(document.Items);
        document.Redo(); Assert.Equal([mosaic], document.Items);
    }

    [Fact]
    public void UndoAndRedoRestoreTheWholeDrawingInOrder()
    {
        var document = new AnnotationDocument();
        var first = Box(); var second = Box(100);
        document.Add(first); document.Add(second);
        Assert.True(document.Undo());
        Assert.Equal([first], document.Items);
        Assert.True(document.Redo());
        Assert.Equal([first, second], document.Items);
    }

    [Fact]
    public void ErasingThenUndoingRestoresTheOriginalLayerOrder()
    {
        var document = new AnnotationDocument();
        var first = Box(); var second = Box(100);
        document.Add(first); document.Add(second);
        Assert.True(document.RemoveAt(0));
        Assert.Equal([second], document.Items);
        document.Undo(); Assert.Equal([first, second], document.Items);
        document.Redo(); Assert.Equal([second], document.Items);
    }

    [Fact]
    public void DrawingAfterUndoDropsTheAbandonedRedoBranch()
    {
        var document = new AnnotationDocument();
        document.Add(Box()); document.Add(Box(100)); document.Undo();
        var replacement = Box(200);
        document.Add(replacement);
        Assert.False(document.Redo());
        Assert.Equal(replacement, document.Items[^1]);
    }

    [Fact]
    public void EmptyOrInvalidMarksDoNotConsumeAnUndoStep()
    {
        var document = new AnnotationDocument();
        Assert.False(document.Add(Box() with { End = new(10, 50) }));
        Assert.False(document.Add(Box() with { Tool = CaptureTool.Select }));
        Assert.False(document.Add(Box() with { Tool = CaptureTool.Text, Text = "  " }));
        Assert.False(document.Add(Box() with { Thickness = double.NaN }));
        Assert.False(document.Add(Box() with { Tool = CaptureTool.Pen, Points = ImmutableArray<PixelPoint>.Empty }));
        Assert.Empty(document.Items); Assert.False(document.CanUndo);
    }

    [Fact]
    public void ResetForANewSelectionDiscardsOldMarksAndTheirHistory()
    {
        var document = new AnnotationDocument();
        document.Add(Box()); document.Undo(); document.Reset();
        Assert.Empty(document.Items);
        Assert.False(document.Undo()); Assert.False(document.Redo());
    }

    [Fact]
    public void RedoPreservesFreehandPointsAndMultilineTextStyle()
    {
        var document = new AnnotationDocument();
        var pen = new ScreenshotAnnotation(CaptureTool.Pen, new(1, 2), new(10, 9), 0xFF2488FF, 4.5,
            Points: [new(1, 2), new(6, 5), new(10, 9)]);
        var text = new ScreenshotAnnotation(CaptureTool.Text, new(12, 15), new(12, 15), 0xFF202735, 3, 30, "说明\n第二行", TextWidth: 240);
        document.Add(pen); document.Add(text); document.Undo(); document.Undo(); document.Redo(); document.Redo();
        Assert.Equal(pen, document.Items[0]); Assert.Equal(text, document.Items[1]);
        Assert.Equal(3, document.Items[0].Points.Length);
    }

    [Fact]
    public void ArrowStylesAndRoutesSurviveRemovalUndoAndRedo()
    {
        var document = new AnnotationDocument();
        var arrows = Enum.GetValues<ArrowStyle>().Select(style => new ScreenshotAnnotation(CaptureTool.Arrow,
            new(10, 80), new(180, 20), 0xFFFF4D4F, 5,
            Points: [new(10, 80), new(70, 80), new(70, 20), new(180, 20)], ArrowStyle: style)).ToArray();
        foreach (var arrow in arrows) Assert.True(document.Add(arrow));
        document.RemoveAt(2); document.Undo();
        Assert.Equal(arrows, document.Items);
        document.Undo(); document.Redo();
        Assert.Equal(arrows, document.Items);
    }

    [Fact]
    public void ClosedArrowRoutesAreValidButEmptyPathsAndUnknownStylesAreRejected()
    {
        var document = new AnnotationDocument();
        var loop = new ScreenshotAnnotation(CaptureTool.Arrow, new(10, 10), new(10, 10), 0xFFFF4D4F, 3,
            Points: [new(10, 10), new(80, 80), new(80, 10), new(10, 10)], ArrowStyle: ArrowStyle.Polyline);
        Assert.True(document.Add(loop));
        Assert.False(document.Add(loop with { Points = [] }));
        Assert.False(document.Add(loop with { Points = [new(10, 10), new(10, 10)] }));
        Assert.False(document.Add(loop with { End = new(80, 80), ArrowStyle = (ArrowStyle)99 }));
        Assert.Equal([loop], document.Items);
    }
}
