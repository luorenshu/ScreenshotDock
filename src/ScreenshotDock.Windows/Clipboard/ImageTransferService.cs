using System.Windows;
using System.Windows.Media.Imaging;
using ScreenshotDock.Windows.Capture;

namespace ScreenshotDock.Windows.Clipboard;

public static class ImageTransferService
{
    public static async Task CopyAsync(BitmapSource bitmap)
    {
        var png = ScreenCaptureService.EncodePng(bitmap);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(bitmap);
                data.SetData("PNG", new MemoryStream(png), autoConvert: false);
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (Exception e) when ((e is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException) && attempt < 5)
            { await Task.Delay(40 * (attempt + 1)); }
        }
    }

    public static DataObject CreateDragData(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("暂存图片不存在。", path);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath(path) });
        data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        data.SetImage(ScreenCaptureService.LoadImage(path));
        return data;
    }
}
