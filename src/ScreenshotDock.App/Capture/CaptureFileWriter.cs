using System.Windows.Media.Imaging;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.App.Capture;

public static class CaptureFileWriter
{
    public static void SavePng(BitmapSource image, string path, string protectedRoot)
    {
        var destination = Path.GetFullPath(path);
        var cache = Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.Equals(cache, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请将图片保存到暂存目录之外，例如桌面或图片文件夹。");
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, ScreenCaptureService.EncodePng(image));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
