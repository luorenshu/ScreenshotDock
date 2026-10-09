using System.Windows.Controls;

namespace ScreenshotDock.App.Controls;

// Each clip stays on the stationary rope even while its photograph scrolls underneath.
public sealed class ClotheslinePanel : Panel
{
    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(nameof(ViewportWidth), typeof(double), typeof(ClotheslinePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(nameof(ScrollOffset), typeof(double), typeof(ClotheslinePanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));
    public double ViewportWidth { get => (double)GetValue(ViewportWidthProperty); set => SetValue(ViewportWidthProperty, value); }
    public double ScrollOffset { get => (double)GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(Math.Max(width, ViewportWidth), height + 30);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var viewport = ViewportWidth > 0 ? ViewportWidth : finalSize.Width;
        var contentWidth = InternalChildren.Cast<UIElement>().Sum(child => child.DesiredSize.Width);
        var x = Math.Max(0, (viewport - contentWidth) / 2);
        foreach (UIElement child in InternalChildren)
        {
            var anchor = x + child.DesiredSize.Width / 2 - ScrollOffset;
            child.Arrange(new Rect(x, ClotheslineRope.HeightAt(anchor, viewport) - 11, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
