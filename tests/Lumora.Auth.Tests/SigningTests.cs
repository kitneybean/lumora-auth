using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Yggdrasil;

namespace Lumora.Auth.Tests;

public class SigningTests
{
    // 1024 bits keeps the tests fast; production keys are 4096.
    private static readonly SigningKey Key = SigningKey.Generate(1024);

    [Fact]
    public void Signature_verifies_with_published_key_and_breaks_on_tampering()
    {
        var signature = Key.Sign("payload");

        Assert.True(SigningKey.Verify(Key.PublicKeyPem, "payload", signature));
        Assert.False(SigningKey.Verify(Key.PublicKeyPem, "payload!", signature));
    }

    [Fact]
    public void Key_file_is_created_once_and_reused()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumora-key-" + Guid.NewGuid().ToString("N"), "key.pem");
        try
        {
            using var first = SigningKey.LoadOrCreate(path, 1024);
            using var second = SigningKey.LoadOrCreate(path, 1024);
            Assert.Equal(first.PublicKeyPem, second.PublicKeyPem);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Textures_value_contains_skin_model_only_for_slim_and_omits_missing_textures()
    {
        var profile = new PlayerProfile { Id = Guid.Parse("11111111-2222-3333-4444-555555555555"), UserId = "u", Name = "Steve" };
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        using (var empty = TexturesProperty.Decode(TexturesProperty.BuildValue(profile, h => "https://x/" + h, at)))
        {
            Assert.Equal("11111111222233334444555555555555", empty.RootElement.GetProperty("profileId").GetString());
            Assert.Equal(1_700_000_000_000, empty.RootElement.GetProperty("timestamp").GetInt64());
            Assert.Empty(empty.RootElement.GetProperty("textures").EnumerateObject());
        }

        profile.SkinHash = "abc";
        using (var classic = TexturesProperty.Decode(TexturesProperty.BuildValue(profile, h => "https://x/" + h, at)))
        {
            var skin = classic.RootElement.GetProperty("textures").GetProperty("SKIN");
            Assert.Equal("https://x/abc", skin.GetProperty("url").GetString());
            Assert.False(skin.TryGetProperty("metadata", out _));
        }

        profile.SkinModel = SkinModel.Slim;
        profile.CapeHash = "cape";
        using var slim = TexturesProperty.Decode(TexturesProperty.BuildValue(profile, h => "https://x/" + h, at));
        var textures = slim.RootElement.GetProperty("textures");
        Assert.Equal("slim", textures.GetProperty("SKIN").GetProperty("metadata").GetProperty("model").GetString());
        Assert.Equal("https://x/cape", textures.GetProperty("CAPE").GetProperty("url").GetString());
    }
}
