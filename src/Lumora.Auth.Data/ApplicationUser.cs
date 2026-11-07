using Lumora.Auth.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace Lumora.Auth.Data;

/// <summary>Site account. Bans use Identity lockout (LockoutEnd).</summary>
public class ApplicationUser : IdentityUser
{
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public string? BanReason { get; set; }

    public PlayerProfile? Profile { get; set; }
}
