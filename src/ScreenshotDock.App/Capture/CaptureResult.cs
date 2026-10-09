using System.Windows.Media.Imaging;
using ScreenshotDock.Core.Models;

namespace ScreenshotDock.App.Capture;

public sealed record CaptureResult(BitmapSource Image, bool ForceCopy = false, string? SavedPath = null, PixelRect? PinBounds = null);
