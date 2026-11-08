namespace Lumora.Auth.Core.Entities;

public enum IpRuleKind
{
    /// <summary>Requests from the network are refused.</summary>
    Block,
    /// <summary>The network is never blocked or auto-banned (admin's own addresses).</summary>
    Allow,
}

/// <summary>A manual block/allow rule or an automatic ban (Source = "auto").</summary>
public class IpRule
{
    public long Id { get; set; }

    /// <summary>IP address or CIDR subnet, e.g. 203.0.113.7 or 203.0.113.0/24.</summary>
    public required string Network { get; set; }

    public IpRuleKind Kind { get; set; }

    /// <summary>"manual" or "auto".</summary>
    public string Source { get; set; } = "manual";

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null = permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public string? CreatedBy { get; set; }
}

/// <summary>Security journal: logins, admin actions, bans, agent commands.</summary>
public class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Short machine-readable action, e.g. "login.site", "admin.build.publish", "ip.ban".</summary>
    public required string Action { get; set; }

    public bool Success { get; set; } = true;

    public string? UserId { get; set; }

    /// <summary>E-mail or player name, kept even if the account is deleted later.</summary>
    public string? UserName { get; set; }

    public string? Ip { get; set; }

    public string? Details { get; set; }
}
