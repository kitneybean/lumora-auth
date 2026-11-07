namespace Lumora.Auth.Core.Entities;

public enum TextureType
{
    Skin,
    Cape,
}

/// <summary>Uploaded PNG texture, stored by content hash.</summary>
public class Texture
{
    /// <summary>SHA-256 hex hash of the normalized image (authlib-injector texture hash).</summary>
    public required string Hash { get; set; }

    public TextureType Type { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}
