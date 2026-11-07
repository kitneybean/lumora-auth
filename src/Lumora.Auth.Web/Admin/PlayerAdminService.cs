using Lumora.Auth.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Admin;

/// <summary>Admin operations on player accounts.</summary>
public sealed class PlayerAdminService(IDbContextFactory<LumoraDbContext> dbFactory, TimeProvider time)
{
    /// <summary>"Permanent" ban end (a real date keeps PostgreSQL and Identity happy).</summary>
    public static readonly DateTimeOffset Forever = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record PlayerRow(
        string UserId, string? Email, string? PlayerName, string? ProfileId, bool HasSkin,
        DateTimeOffset RegisteredAt, bool IsAdmin, string? BanReason, DateTimeOffset? BannedUntil);

    public async Task<(List<PlayerRow> Rows, int Total)> SearchAsync(string? query, int skip, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstOrDefaultAsync(ct);
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToUpperInvariant();
            var name = query.Trim();
            // Nicknames use a case-insensitive collation that supports equality only.
            users = users.Where(u => u.NormalizedEmail!.Contains(q) || (u.Profile != null && u.Profile.Name == name));
        }

        var total = await users.CountAsync(ct);
        var now = time.GetUtcNow();
        var rows = await users
            .OrderByDescending(u => u.RegisteredAt)
            .Skip(skip).Take(take)
            .Select(u => new
            {
                u.Id, u.Email, Name = u.Profile != null ? u.Profile.Name : null, ProfileId = u.Profile != null ? (Guid?)u.Profile.Id : null,
                HasSkin = u.Profile != null && u.Profile.SkinHash != null, u.RegisteredAt,
                IsAdmin = db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId),
                u.BanReason, u.LockoutEnd,
            })
            .ToListAsync(ct);

        return (rows.Select(u => new PlayerRow(u.Id, u.Email, u.Name, u.ProfileId?.ToString("N"), u.HasSkin, u.RegisteredAt, u.IsAdmin,
            u.BanReason is not null && u.LockoutEnd > now ? u.BanReason : null,
            u.BanReason is not null && u.LockoutEnd > now ? u.LockoutEnd : null)).ToList(), total);
    }

    /// <summary>Bans the account (null <paramref name="until"/> = forever) and revokes its launcher tokens.</summary>
    public async Task BanAsync(string userId, string reason, DateTimeOffset? until, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var end = until ?? Forever;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.BanReason, reason.Trim())
            .SetProperty(u => u.LockoutEnabled, true)
            .SetProperty(u => u.LockoutEnd, end)
            // Changing the stamp signs the user out of the site too.
            .SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), ct);
        await db.Tokens.Where(t => t.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), ct);
    }

    public async Task UnbanAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.BanReason, (string?)null)
            .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null)
            .SetProperty(u => u.AccessFailedCount, 0), ct);
    }

    public async Task SetAdminAsync(string userId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var roleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstAsync(ct);
        var has = await db.UserRoles.AnyAsync(r => r.UserId == userId && r.RoleId == roleId, ct);
        if (isAdmin && !has)
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
        else if (!isAdmin && has)
            await db.UserRoles.Where(r => r.UserId == userId && r.RoleId == roleId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        // Role claims live in the cookie: force re-login so the change applies.
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), ct);
    }

    public async Task ResetSkinAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Profiles.Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SkinHash, (string?)null), ct);
    }

    public async Task<(int Players, int Builds, int PublishedBuilds, int Servers)> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Users.CountAsync(ct), await db.Builds.CountAsync(ct),
            await db.Builds.CountAsync(b => b.IsPublished, ct), await db.Servers.CountAsync(ct));
    }
}
