namespace Lumora.Auth.Core.Entities;

/// <summary>Access token issued by /authserver/authenticate.</summary>
public class YggdrasilToken
{
    public required string AccessToken { get; set; }

    public required string ClientToken { get; set; }

    public required string UserId { get; set; }

    /// <summary>Selected profile bound to the token.</summary>
    public Guid? ProfileId { get; set; }

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>After this moment the token cannot be used or refreshed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Revoked tokens stay in the table until cleanup.</summary>
    public bool IsRevoked { get; set; }
}
