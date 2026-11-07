using SkiaSharp;

namespace Lumora.Auth.Core.Textures;

/// <summary>Server list icons: Minecraft expects a 64×64 PNG (base64 in servers.dat).</summary>
public static class ServerIcon
{
    public const int Size = 64;
    public const int MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>Decodes any common image format, center-crops it to a square and scales it to 64×64 PNG.</summary>
    /// <exception cref="TextureImages.InvalidTextureException">Russian message.</exception>
    public static byte[] Process(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxUploadBytes)
            throw new TextureImages.InvalidTextureException("Иконка должна быть картинкой до 2 МБ.");

        using var source = SKBitmap.Decode(data);
        if (source is null)
            throw new TextureImages.InvalidTextureException("Не удалось прочитать картинку (нужен PNG, JPEG или WebP).");

        var side = Math.Min(source.Width, source.Height);
        var crop = new SKRectI((source.Width - side) / 2, (source.Height - side) / 2, (source.Width + side) / 2, (source.Height + side) / 2);
        using var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.ExtractSubset(square, crop))
            throw new TextureImages.InvalidTextureException("Не удалось обработать картинку.");

        // Pixel art (small icons) stays crisp; photos are smoothed.
        var sampling = side <= Size ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var scaled = square.Resize(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul), sampling)
            ?? throw new TextureImages.InvalidTextureException("Не удалось уменьшить картинку.");
        using var image = SKImage.FromBitmap(scaled);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
