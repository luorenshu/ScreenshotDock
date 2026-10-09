using System.Windows.Input;
using ScreenshotDock.App.ViewModels;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.App.Views;

public partial class PreviewWindow : Window
{
    private readonly ScreenshotCard _card;
    public event Action<ScreenshotCard>? CopyRequested;
    public event Action<ScreenshotCard>? SaveRequested;
    public PreviewWindow(ScreenshotCard card)
    {
        InitializeComponent();
        _card = card;
        PreviewImage.Source = ScreenCaptureService.LoadImage(card.FilePath);
        DimensionsLabel.Text = card.SizeText;
        InfoLabel.Text = $"{card.TimeText}   ·   {card.FileSizeText}   ·   PNG";
    }
    private void CopyClick(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(_card);
    private void SaveClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(_card);
    private void WindowKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
}
