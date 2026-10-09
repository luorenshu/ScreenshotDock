using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenshotDock.Core.Models;
using ScreenshotDock.Windows.Native;

namespace ScreenshotDock.Windows.Capture;

public sealed record DesktopFrame(BitmapSource Bitmap, PixelRect Bounds);

public static class ScreenCaptureService
{
    public static DesktopFrame CaptureDesktop()
    {
        var bounds = DesktopEnvironment.VirtualBounds;
        var screen = NativeMethods.GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = NativeMethods.CreateCompatibleDC(screen);
            bitmap = NativeMethods.CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (memory == 0 || bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(memory, bitmap);
            if (!NativeMethods.BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return new(image, bounds);
        }
        finally
        {
            if (previous != 0 && memory != 0) NativeMethods.SelectObject(memory, previous);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(0, screen);
        }
    }

    public static BitmapSource Crop(DesktopFrame frame, PixelRect selection)
    {
        var region = selection.Intersect(frame.Bounds);
        if (region.IsEmpty) throw new ArgumentException("请先选择截图区域。", nameof(selection));
        var cropped = new CroppedBitmap(frame.Bitmap, new Int32Rect(region.X - frame.Bounds.X, region.Y - frame.Bounds.Y, region.Width, region.Height));
        cropped.Freeze();
        return cropped;
    }

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] Thumbnail(BitmapSource bitmap)
    {
        var factor = Math.Min(1, Math.Min(320.0 / bitmap.PixelWidth, 200.0 / bitmap.PixelHeight));
        var thumbnail = new TransformedBitmap(bitmap, new ScaleTransform(factor, factor));
        thumbnail.Freeze();
        return EncodePng(thumbnail);
    }

    public static BitmapSource LoadImage(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
