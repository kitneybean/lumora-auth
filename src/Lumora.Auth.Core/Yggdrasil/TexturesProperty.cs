using System.Text;
using System.Text.Json;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Profiles;

namespace Lumora.Auth.Core.Yggdrasil;

/// <summary>Builds the base64 <c>textures</c> profile property.</summary>
public static class TexturesProperty
{
    public const string Name = "textures";

    /// <param name="textureUrl">Maps a texture hash to its public URL.</param>
    public static string BuildValue(PlayerProfile profile, Func<string, string> textureUrl, DateTimeOffset timestamp)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("timestamp", timestamp.ToUnixTimeMilliseconds());
            w.WriteString("profileId", MinecraftUuid.ToUnsigned(profile.Id));
            w.WriteString("profileName", profile.Name);
            w.WriteStartObject("textures");
            if (profile.SkinHash is { } skin)
            {
                w.WriteStartObject("SKIN");
                w.WriteString("url", textureUrl(skin));
                if (profile.SkinModel == SkinModel.Slim)
                {
                    w.WriteStartObject("metadata");
                    w.WriteString("model", "slim");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            if (profile.CapeHash is { } cape)
            {
                w.WriteStartObject("CAPE");
                w.WriteString("url", textureUrl(cape));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    public static JsonDocument Decode(string value) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
}
