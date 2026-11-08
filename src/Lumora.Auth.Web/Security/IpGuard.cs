using System.Collections.Concurrent;
using System.Net;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Security;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// Decides whether an IP may use the site: admin allow list, then manual blocks and automatic bans,
/// then public blocklists of known botnets. Rules are cached in memory; the request path never touches the database.
/// </summary>
public sealed class IpGuard(IDbContextFactory<LumoraDbContext> dbFactory, AuditService audit, ILogger<IpGuard> logger)
{
    private sealed record CachedRule(long Id, IpNetwork Network, IpRuleKind Kind, DateTimeOffset? ExpiresAt, string? Reason, string Source);

    private volatile IReadOnlyList<CachedRule> _rules = [];
    private volatile IpRangeSet _blocklist = IpRangeSet.Empty;
    private readonly ConcurrentDictionary<string, long> _blockedByReason = new();

    public DateTimeOffset? BlocklistUpdatedAt { get; private set; }
    public int BlocklistRanges => _blocklist.Count;
    public IReadOnlyDictionary<string, long> BlockedCounts => _blockedByReason;

    public enum Verdict { Allowed, AllowListed, Blocked }

    public (Verdict Verdict, string? Reason) Check(IPAddress? ip)
    {
        if (ip is null || IpNetwork.IsPrivateOrLoopback(ip))
            return (Verdict.Allowed, null);

        var now = DateTimeOffset.UtcNow;
        var rules = _rules;
        foreach (var r in rules)
            if (r.Kind == IpRuleKind.Allow && r.Network.Contains(ip))
                return (Verdict.AllowListed, null);
        foreach (var r in rules)
            if (r.Kind == IpRuleKind.Block && (r.ExpiresAt is null || r.ExpiresAt > now) && r.Network.Contains(ip))
                return (Verdict.Blocked, r.Source == "auto" ? "автобан" : "ручная блокировка");
        if (_blocklist.Contains(ip))
            return (Verdict.Blocked, "список ботнетов");
        return (Verdict.Allowed, null);
    }

    public bool IsAllowListed(IPAddress ip) => Check(ip).Verdict == Verdict.AllowListed || IpNetwork.IsPrivateOrLoopback(ip);

    public void CountBlocked(string reason) => _blockedByReason.AddOrUpdate(reason, 1, (_, n) => n + 1);

    public void SetBlocklist(IpRangeSet set, DateTimeOffset updatedAt)
    {
        _blocklist = set;
        BlocklistUpdatedAt = updatedAt;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.IpRules.AsNoTracking().Where(r => r.ExpiresAt == null || r.ExpiresAt > now).ToListAsync(ct);
        var list = new List<CachedRule>();
        foreach (var r in rows)
        {
            if (IpNetwork.TryParse(r.Network, out var n))
                list.Add(new CachedRule(r.Id, n, r.Kind, r.ExpiresAt, r.Reason, r.Source));
            else
                logger.LogWarning("Ignoring invalid IP rule {Network}", r.Network);
        }
        _rules = list;
    }

    public async Task<List<IpRule>> ListAsync(bool includeExpired = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.IpRules.AsNoTracking()
            .Where(r => includeExpired || r.ExpiresAt == null || r.ExpiresAt > now)
            .OrderByDescending(r => r.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
    }

    /// <summary>Adds a manual rule. Returns an error message or null.</summary>
    public async Task<string?> AddManualAsync(string network, IpRuleKind kind, string? reason, TimeSpan? duration, string? createdBy, CancellationToken ct = default)
    {
        if (!IpNetwork.TryParse(network, out var parsed))
            return "Неверный адрес: нужен IP (203.0.113.7) или подсеть (203.0.113.0/24).";
        if (kind == IpRuleKind.Block && parsed.End - parsed.Start > (UInt128)ushort.MaxValue * 256 && !network.Contains(':'))
            return "Слишком большая подсеть для блокировки (больше /8).";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.IpRules.Add(new IpRule
        {
            Network = network.Trim(),
            Kind = kind,
            Source = "manual",
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ExpiresAt = duration is null ? null : DateTimeOffset.UtcNow + duration,
            CreatedBy = createdBy,
        });
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        audit.Log(kind == IpRuleKind.Block ? "ip.block" : "ip.allow", userName: createdBy, details: $"{network} {reason}");
        return null;
    }

    public async Task RemoveAsync(long id, string? removedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.IpRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
            return;
        db.IpRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        audit.Log("ip.unblock", userName: removedBy, details: $"{rule.Kind} {rule.Network}");
    }

    /// <summary>Automatic ban; repeated offenders get longer bans (1 h, 24 h, 7 days).</summary>
    public async Task<TimeSpan> AutoBanAsync(IPAddress ip, string reason, CancellationToken ct = default)
    {
        var network = ip.ToString();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var previous = await db.IpRules.CountAsync(r => r.Network == network && r.Source == "auto" && r.CreatedAt > since, ct);
        var duration = previous switch { 0 => TimeSpan.FromHours(1), 1 => TimeSpan.FromHours(24), _ => TimeSpan.FromDays(7) };
        db.IpRules.Add(new IpRule
        {
            Network = network,
            Kind = IpRuleKind.Block,
            Source = "auto",
            Reason = reason,
            ExpiresAt = DateTimeOffset.UtcNow + duration,
        });
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        audit.Log("ip.autoban", ip: network, details: $"{reason}; на {duration.TotalHours:0} ч");
        logger.LogWarning("Auto-banned {Ip} for {Hours} h: {Reason}", network, duration.TotalHours, reason);
        return duration;
    }
}
