using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenshotDock.App.Capture;

internal static class MosaicRenderer
{
    // Only small, averaged grids are retained. Source keys can be collected when the selection changes.
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<int, BitmapSource>> Grids = new();

    public static BitmapSource Grid(BitmapSource source, int blockSize)
    {
        if (blockSize is < 2 or > 512) throw new ArgumentOutOfRangeException(nameof(blockSize));
        var grids = Grids.GetOrCreateValue(source);
        lock (grids)
        {
            if (grids.TryGetValue(blockSize, out var cached)) return cached;
            var input = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var stride = checked(source.PixelWidth * 4);
            var pixels = new byte[checked(stride * source.PixelHeight)];
            input.CopyPixels(pixels, stride, 0);
            var width = (source.PixelWidth + blockSize - 1) / blockSize;
            var height = (source.PixelHeight + blockSize - 1) / blockSize;
            var result = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var right = Math.Min(source.PixelWidth, (column + 1) * blockSize);
                    var bottom = Math.Min(source.PixelHeight, (row + 1) * blockSize);
                    var blue = 0; var green = 0; var red = 0; var alpha = 0;
                    for (var y = row * blockSize; y < bottom; y++)
                        for (var x = column * blockSize; x < right; x++)
                        {
                            var offset = y * stride + x * 4;
                            blue += pixels[offset]; green += pixels[offset + 1];
                            red += pixels[offset + 2]; alpha += pixels[offset + 3];
                        }
                    var count = (right - column * blockSize) * (bottom - row * blockSize);
                    var target = (row * width + column) * 4;
                    result[target] = (byte)(blue / count); result[target + 1] = (byte)(green / count);
                    result[target + 2] = (byte)(red / count); result[target + 3] = (byte)(alpha / count);
                }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, result, width * 4);
            image.Freeze();
            grids.Add(blockSize, image);
            return image;
        }
    }
}
