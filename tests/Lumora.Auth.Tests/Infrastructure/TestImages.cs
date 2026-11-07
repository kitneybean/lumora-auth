using SkiaSharp;

namespace Lumora.Auth.Tests.Infrastructure;

public static class TestImages
{
    /// <summary>PNG of the given size; <paramref name="paint"/> sets RGBA per pixel.</summary>
    public static byte[] Png(int width, int height, Func<int, int, SKColor>? paint = null, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, paint?.Invoke(x, y) ?? new SKColor((byte)(x * 4), (byte)(y * 4), 128, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
