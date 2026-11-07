namespace Lumora.Auth.Core.Entities;

public enum SkinModel
{
    Classic,
    Slim,
}

/// <summary>In-game identity of a user (one per account).</summary>
public class PlayerProfile
{
    /// <summary>Minecraft UUID of the player.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning Identity user id.</summary>
    public required string UserId { get; set; }

    /// <summary>In-game name, 3-16 chars of [A-Za-z0-9_].</summary>
    public required string Name { get; set; }

    public SkinModel SkinModel { get; set; } = SkinModel.Classic;

    /// <summary>Hash of the skin texture, null for default skin.</summary>
    public string? SkinHash { get; set; }

    /// <summary>Hash of the cape texture, if any.</summary>
    public string? CapeHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
