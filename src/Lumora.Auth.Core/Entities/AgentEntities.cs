namespace Lumora.Auth.Core.Entities;

/// <summary>A machine running Lumora Agent, which installs and runs game servers on behalf of the site.</summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>SHA-256 (hex) of the agent key; the key itself is shown to nobody and stored only on the agent.</summary>
    public required string KeyHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public string? LastIp { get; set; }

    public string? Version { get; set; }

    public string? Os { get; set; }

    public string? Hostname { get; set; }

    public int CpuCount { get; set; }

    public long MemoryMb { get; set; }

    public bool IsRevoked { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public List<GameServer> Servers { get; set; } = [];
}

/// <summary>One-time token that lets a new agent register (valid for 30 minutes).</summary>
public class AgentJoinToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 (hex) of the token.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Name for the agent created with this token.</summary>
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public Guid? AgentId { get; set; }

    public string? CreatedBy { get; set; }
}
