using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.App.Views;

// A separate click-through layer keeps the shelf's transparent gaps interactive with the desktop.
public sealed class DockBackdropWindow : Window
{
    private readonly Border _shade;
    public double ShadeOpacity => _shade.Opacity;

    public DockBackdropWindow()
    {
        Title = "ScreenshotDock · 顶部渐变";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Width = 1000;
        Height = 328;
        // The wide ellipse fades along an arc: raised sides and a gently lowered center.
        var gradient = new RadialGradientBrush
        {
            Center = new Point(0.5, -0.10),
            GradientOrigin = new Point(0.5, -0.10),
            RadiusX = 0.86,
            RadiusY = 1.10
        };
        gradient.GradientStops.Add(new(Color.FromArgb(190, 12, 26, 50), 0));
        gradient.GradientStops.Add(new(Color.FromArgb(180, 12, 26, 50), 0.32));
        gradient.GradientStops.Add(new(Color.FromArgb(136, 12, 26, 50), 0.58));
        gradient.GradientStops.Add(new(Color.FromArgb(66, 12, 26, 50), 0.78));
        gradient.GradientStops.Add(new(Color.FromArgb(12, 12, 26, 50), 0.94));
        gradient.GradientStops.Add(new(Color.FromArgb(0, 12, 26, 50), 1));
        gradient.Freeze();
        _shade = new Border { Background = gradient, IsHitTestVisible = false };
        Content = _shade;
        SourceInitialized += (_, _) =>
        {
            DesktopEnvironment.DoNotActivate(this);
            DesktopEnvironment.MakeClickThrough(this);
            DesktopEnvironment.ExcludeFromCapture(this);
        };
    }

    public void SetShadeOpacity(double opacity)
    {
        _shade.BeginAnimation(OpacityProperty, null);
        _shade.Opacity = opacity;
    }

    public void FadeShade(double opacity, TimeSpan duration)
    {
        var start = _shade.Opacity;
        _shade.Opacity = opacity;
        _shade.BeginAnimation(OpacityProperty, new DoubleAnimation(start, opacity, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
