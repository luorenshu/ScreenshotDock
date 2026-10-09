using System.ComponentModel;
using System.Windows.Media.Imaging;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Core.Services;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.App.ViewModels;

public sealed class ScreenshotCard : INotifyPropertyChanged
{
    private bool _isCopied;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ScreenshotItem Item { get; }
    public string FilePath { get; }
    public BitmapSource Thumbnail { get; }
    public double Tilt { get; set; }
    public double PhotoWidth => Math.Max(104, Item.Width * PhotoScale);
    public double PhotoHeight => Math.Max(78, Item.Height * PhotoScale);
    private double PhotoScale => Math.Min(224.0 / Item.Width, 146.0 / Item.Height);
    public Thickness CaptionMargin => new(0, PhotoHeight + 26, 0, 0);
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (_isCopied == value) return;
            _isCopied = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCopied)));
        }
    }
    public string SizeText => $"{Item.Width} × {Item.Height}";
    public string TimeText => Item.CreatedAt.ToLocalTime().ToString("MM/dd  HH:mm");
    public string FileSizeText => Item.FileSize < 1024 * 1024 ? $"{Item.FileSize / 1024.0:0.#} KB" : $"{Item.FileSize / 1024.0 / 1024:0.#} MB";
    public string Tooltip => $"{TimeText} · {SizeText} · {FileSizeText}\n单击复制 · 双击预览 · 拖拽分享";
    public ScreenshotCard(ScreenshotItem item, ScreenshotRepository repository)
    {
        Item = item;
        FilePath = repository.GetImagePath(item.Id);
        var thumbnail = repository.GetThumbnailPath(item.Id);
        Thumbnail = ScreenCaptureService.LoadImage(File.Exists(thumbnail) ? thumbnail : FilePath, 320);
    }
}
