using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Profiles;
using Lumora.Auth.Core.Yggdrasil;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Yggdrasil;

/// <summary>Serializes player profiles with their (optionally signed) properties.</summary>
public sealed class ProfileWriter(SigningKey key, IOptions<LumoraOptions> options, TimeProvider time)
{
    public string PublicUrl => options.Value.PublicUrl.TrimEnd('/');

    public string TextureUrl(string hash) => $"{PublicUrl}/textures/{hash}";

    public GameProfileDto Write(PlayerProfile profile, bool signed)
    {
        var textures = TexturesProperty.BuildValue(profile, TextureUrl, time.GetUtcNow());
        // Players can upload skins; capes are given out by administrators.
        const string uploadable = "skin";
        return new GameProfileDto(MinecraftUuid.ToUnsigned(profile.Id), profile.Name,
        [
            new PropertyDto(TexturesProperty.Name, textures, signed ? key.Sign(textures) : null),
            new PropertyDto("uploadableTextures", uploadable, signed ? key.Sign(uploadable) : null),
        ]);
    }

    public static ProfileRef Ref(PlayerProfile profile) => new(MinecraftUuid.ToUnsigned(profile.Id), profile.Name);
}
