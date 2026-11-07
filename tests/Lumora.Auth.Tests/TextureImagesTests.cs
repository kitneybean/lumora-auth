using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Textures;
using Lumora.Auth.Tests.Infrastructure;
using SkiaSharp;

namespace Lumora.Auth.Tests;

public class TextureImagesTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(64, 32)]
    public void Accepts_standard_skin_sizes(int w, int h)
    {
        var image = TextureImages.Process(TestImages.Png(w, h), TextureType.Skin);

        Assert.Equal((w, h), (image.Width, image.Height));
        Assert.Matches("^[0-9a-f]{64}$", image.Hash);
        using var decoded = SKBitmap.Decode(image.Png);
        Assert.Equal(w, decoded.Width);
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(128, 128)]
    [InlineData(64, 48)]
    public void Rejects_other_sizes(int w, int h)
    {
        var e = Assert.Throws<TextureImages.InvalidTextureException>(() => TextureImages.Process(TestImages.Png(w, h), TextureType.Skin));
        Assert.Contains($"{w}×{h}", e.Message);
    }

    [Fact]
    public void Rejects_non_png_and_garbage()
    {
        Assert.Throws<TextureImages.InvalidTextureException>(() =>
            TextureImages.Process(TestImages.Png(64, 64, format: SKEncodedImageFormat.Jpeg), TextureType.Skin));
        Assert.Throws<TextureImages.InvalidTextureException>(() =>
            TextureImages.Process("not an image"u8.ToArray(), TextureType.Skin));
        Assert.Throws<TextureImages.InvalidTextureException>(() =>
            TextureImages.Process([], TextureType.Skin));
        Assert.Throws<TextureImages.InvalidTextureException>(() =>
            TextureImages.Process(new byte[TextureImages.MaxUploadBytes + 1], TextureType.Skin));
    }

    [Fact]
    public void Hash_ignores_color_of_fully_transparent_pixels()
    {
        var a = TestImages.Png(64, 64, (x, y) => x < 8 ? new SKColor(255, 0, 0, 0) : new SKColor(10, 20, 30, 255));
        var b = TestImages.Png(64, 64, (x, y) => x < 8 ? new SKColor(0, 255, 0, 0) : new SKColor(10, 20, 30, 255));
        var c = TestImages.Png(64, 64, (x, y) => x < 8 ? new SKColor(0, 255, 0, 0) : new SKColor(10, 20, 31, 255));

        var ha = TextureImages.Process(a, TextureType.Skin).Hash;
        Assert.Equal(ha, TextureImages.Process(b, TextureType.Skin).Hash);
        Assert.NotEqual(ha, TextureImages.Process(c, TextureType.Skin).Hash);
    }

    [Fact]
    public void Reencoding_is_stable()
    {
        var first = TextureImages.Process(TestImages.Png(64, 64), TextureType.Skin);
        var second = TextureImages.Process(first.Png, TextureType.Skin);
        Assert.Equal(first.Hash, second.Hash);
    }
}
