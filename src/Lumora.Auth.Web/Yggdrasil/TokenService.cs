using System.Security.Cryptography;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Yggdrasil;

/// <summary>
/// Issues and checks launcher access tokens.
/// A token can be used until <see cref="YggdrasilToken.ExpiresAt"/> and refreshed for
/// <see cref="LumoraOptions.TokenRefreshWindow"/> after that. Tokens of banned users are unusable.
/// </summary>
public sealed class TokenService(LumoraDbContext db, IOptions<LumoraOptions> options, Settings.SiteSettingsService site, TimeProvider time)
{
    private LumoraOptions Options => options.Value;

    public async Task<YggdrasilToken> IssueAsync(string userId, string clientToken, Guid? profileId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var refreshDeadline = now - Options.TokenRefreshWindow;

        // Drop dead tokens and keep at most MaxTokensPerUser live ones.
        var tokens = await db.Tokens.Where(t => t.UserId == userId).OrderBy(t => t.IssuedAt).ToListAsync(ct);
        var dead = tokens.Where(t => t.IsRevoked || t.ExpiresAt < refreshDeadline).ToList();
        db.Tokens.RemoveRange(dead);
        var live = tokens.Except(dead).ToList();
        foreach (var old in live.Take(Math.Max(0, live.Count - site.Current.MaxTokensPerUser + 1)))
            db.Tokens.Remove(old);

        var token = new YggdrasilToken
        {
            AccessToken = NewToken(),
            ClientToken = clientToken,
            UserId = userId,
            ProfileId = profileId,
            IssuedAt = now,
            ExpiresAt = now + TimeSpan.FromDays(site.Current.TokenLifetimeDays),
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Token that may be used to join servers right now.</summary>
    public async Task<YggdrasilToken?> FindUsableAsync(string? accessToken, string? clientToken, CancellationToken ct = default)
    {
        var token = await FindAsync(accessToken, clientToken, ct);
        return token is not null && token.ExpiresAt > time.GetUtcNow() && !await IsBannedAsync(token.UserId, ct)
            ? token
            : null;
    }

    /// <summary>Token that may be exchanged for a new one.</summary>
    public async Task<YggdrasilToken?> FindRefreshableAsync(string? accessToken, string? clientToken, CancellationToken ct = default)
    {
        var token = await FindAsync(accessToken, clientToken, ct);
        return token is not null && token.ExpiresAt + Options.TokenRefreshWindow > time.GetUtcNow() && !await IsBannedAsync(token.UserId, ct)
            ? token
            : null;
    }

    public async Task RevokeAsync(string? accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accessToken))
            return;
        await db.Tokens.Where(t => t.AccessToken == accessToken)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), ct);
    }

    public async Task RevokeAllAsync(string userId, CancellationToken ct = default) =>
        await db.Tokens.Where(t => t.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), ct);

    private async Task<YggdrasilToken?> FindAsync(string? accessToken, string? clientToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accessToken))
            return null;
        var token = await db.Tokens.FirstOrDefaultAsync(t => t.AccessToken == accessToken, ct);
        if (token is null || token.IsRevoked)
            return null;
        if (!string.IsNullOrEmpty(clientToken) && token.ClientToken != clientToken)
            return null;
        return token;
    }

    private Task<bool> IsBannedAsync(string userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.Users.AnyAsync(u => u.Id == userId && u.BanReason != null && u.LockoutEnd != null && u.LockoutEnd > now, ct);
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
