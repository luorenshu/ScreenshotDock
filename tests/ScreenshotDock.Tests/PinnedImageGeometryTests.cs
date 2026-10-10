using ScreenshotDock.Core.Models;
using Xunit;

namespace ScreenshotDock.Tests;

public sealed class PinnedImageGeometryTests
{
    [Theory]
    [InlineData(PinnedResizeEdge.Right, 100, 0, -800, 40, 500, 250)]
    [InlineData(PinnedResizeEdge.Left, -100, 0, -900, 40, 500, 250)]
    [InlineData(PinnedResizeEdge.Top, 0, -50, -850, 15, 500, 250)]
    [InlineData(PinnedResizeEdge.Bottom, 0, 50, -850, 65, 500, 250)]
    [InlineData(PinnedResizeEdge.Left | PinnedResizeEdge.Top, -100, -50, -900, 15, 500, 250)]
    [InlineData(PinnedResizeEdge.Right | PinnedResizeEdge.Top, 100, -50, -800, 15, 500, 250)]
    [InlineData(PinnedResizeEdge.Left | PinnedResizeEdge.Bottom, -100, 50, -900, 65, 500, 250)]
    [InlineData(PinnedResizeEdge.Right | PinnedResizeEdge.Bottom, 100, 50, -800, 65, 500, 250)]
    public void ResizingPreservesAspectAndOppositeAnchorIncludingNegativeScreenCoordinates(
        PinnedResizeEdge edges, int dx, int dy, int x, int y, int width, int height) =>
        Assert.Equal(new PixelRect(x, y, width, height), PinnedImageGeometry.Resize(new(-800, 65, 400, 200), edges, new(dx, dy), 32));

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    public void ShrinkingPastTheOppositeCornerStopsAtTheDpiAdjustedMinimum(int minimum)
    {
        var result = PinnedImageGeometry.Resize(new(100, 200, 400, 200), PinnedResizeEdge.Left | PinnedResizeEdge.Top, new(2000, 2000), minimum);
        Assert.Equal(new PixelRect(500 - minimum * 2, 400 - minimum, minimum * 2, minimum), result);
    }

    [Fact]
    public void CornerCanBeResizedWithOnlyVerticalMovement()
    {
        var result = PinnedImageGeometry.Resize(new(10, 20, 400, 200), PinnedResizeEdge.Right | PinnedResizeEdge.Bottom, new(0, 100), 32);
        Assert.Equal(new PixelRect(10, 20, 440, 220), result);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    public void EdgeAndCornerHitAreasScaleWithoutSwallowingTheImageInterior(int tolerance)
    {
        var bounds = new PixelRect(-500, -200, 400, 200);
        Assert.Equal(PinnedResizeEdge.Left, PinnedImageGeometry.HitTest(bounds, new(-500 + tolerance, -100), tolerance));
        Assert.Equal(PinnedResizeEdge.Right | PinnedResizeEdge.Bottom, PinnedImageGeometry.HitTest(bounds, new(-100 - tolerance * 2, -tolerance * 2), tolerance));
        Assert.Equal(PinnedResizeEdge.None, PinnedImageGeometry.HitTest(bounds, new(-300, -100), tolerance));
        Assert.Equal(PinnedResizeEdge.None, PinnedImageGeometry.HitTest(bounds, new(-500 - tolerance - 1, -100), tolerance));
    }

    [Fact]
    public void OddAspectRatiosStayWithinHalfAPixelOfTheirOriginalProportions()
    {
        var original = new PixelRect(20, 30, 333, 197);
        var result = PinnedImageGeometry.Resize(original, PinnedResizeEdge.Right, new(107, 0), 32);
        Assert.Equal(440, result.Width);
        Assert.InRange(Math.Abs(result.Height - result.Width * 197.0 / 333), 0, 0.5);
    }

    [Fact]
    public void RepeatedResizeGesturesUseTheSourceAspectInsteadOfAccumulatingRoundedRatios()
    {
        var bounds = new PixelRect(20, 30, 333, 197);
        for (var i = 0; i < 100; i++)
        {
            bounds = PinnedImageGeometry.Resize(bounds, PinnedResizeEdge.Right, new(i % 2 == 0 ? 47 : -43, 0), 32, 333.0 / 197);
            Assert.InRange(Math.Abs(bounds.Height - bounds.Width * 197.0 / 333), 0, 0.5);
        }
    }
}
