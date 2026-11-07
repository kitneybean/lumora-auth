using System.Buffers.Binary;
using System.Security.Cryptography;
using Lumora.Auth.Core.Entities;
using SkiaSharp;

namespace Lumora.Auth.Core.Textures;

/// <summary>A validated, re-encoded texture ready to be stored.</summary>
public sealed record TextureImage(string Hash, byte[] Png, int Width, int Height, TextureType Type);

/// <summary>
/// Checks uploaded skins and capes and re-encodes them as clean PNG (no metadata,
/// fully transparent pixels zeroed). The hash follows the authlib-injector texture hash rule.
/// </summary>
public static class TextureImages
{
    public const int MaxUploadBytes = 256 * 1024;

    public sealed class InvalidTextureException(string message) : Exception(message);

    /// <exception cref="InvalidTextureException">Russian message suitable for the user.</exception>
    public static TextureImage Process(ReadOnlySpan<byte> data, TextureType type)
    {
        if (data.Length == 0)
            throw new InvalidTextureException("Файл пустой.");
        if (data.Length > MaxUploadBytes)
            throw new InvalidTextureException($"Файл слишком большой (больше {MaxUploadBytes / 1024} КБ).");

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
            throw new InvalidTextureException("Нужен файл PNG.");

        var (w, h) = (codec.Info.Width, codec.Info.Height);
        var allowed = type == TextureType.Skin
            ? (w == 64 && (h == 64 || h == 32))
            : (w == 64 && h == 32) || (w == 22 && h == 17);
        if (!allowed)
        {
            throw new InvalidTextureException(type == TextureType.Skin
                ? $"Скин должен быть 64×64 (или старый формат 64×32), а у файла {w}×{h}."
                : $"Плащ должен быть 64×32, а у файла {w}×{h}.");
        }

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidTextureException("Не удалось прочитать PNG.");

        var pixels = bitmap.GetPixelSpan().ToArray();
        // Fully transparent pixels carry no color: zero them so equal-looking images match.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new TextureImage(ComputeHash(pixels, w, h), png.ToArray(), w, h, type);
    }

    /// <summary>
    /// SHA-256 over width, height (big-endian int32) and the pixels column by column as ARGB,
    /// with fully transparent pixels written as zero.
    /// </summary>
    public static string ComputeHash(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var buffer = new byte[8 + width * height * 4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, width);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), height);
        var o = 8;
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var i = (y * width + x) * 4;
                var a = rgba[i + 3];
                if (a != 0)
                {
                    buffer[o] = a;
                    buffer[o + 1] = rgba[i];
                    buffer[o + 2] = rgba[i + 1];
                    buffer[o + 3] = rgba[i + 2];
                }
                o += 4;
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }
}
