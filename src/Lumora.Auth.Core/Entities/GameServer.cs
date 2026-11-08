namespace Lumora.Auth.Core.Entities;

/// <summary>A Minecraft server shown in the launcher and written to servers.dat.</summary>
public class GameServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Address { get; set; }

    public int Port { get; set; } = 25565;

    /// <summary>64x64 PNG icon as base64 (same format as servers.dat).</summary>
    public string? IconBase64 { get; set; }

    public Guid? GameBuildId { get; set; }

    public GameBuild? GameBuild { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Launcher adds this server to the in-game server list.</summary>
    public bool AddToServerList { get; set; } = true;

    /// <summary>Launcher starts the game with --quickPlayMultiplayer to this server.</summary>
    public bool AutoConnect { get; set; }

    public bool IsEnabled { get; set; } = true;

    // ---- Managed by Lumora Agent (all optional: a server can also be just an address) ----

    /// <summary>Agent that installs and runs this server; null for servers run by hand.</summary>
    public Guid? AgentId { get; set; }

    public Agent? Agent { get; set; }

    /// <summary>Java heap for the server (-Xmx), MB.</summary>
    public int MemoryMb { get; set; } = 4096;

    /// <summary>Extra JVM arguments, one per line.</summary>
    public string? ExtraJvmArgs { get; set; }

    /// <summary>Start together with the agent (machine boot).</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Start again after a crash.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Daily restart times "HH:mm", comma separated (agent local time).</summary>
    public string? RestartTimes { get; set; }

    /// <summary>Daily world backup times "HH:mm", comma separated.</summary>
    public string? BackupTimes { get; set; }

    public int BackupsToKeep { get; set; } = 7;

    /// <summary>server.properties values forced by the site, "key=value" per line.</summary>
    public string? ServerProperties { get; set; }

    /// <summary>When and by whom the Minecraft EULA was accepted for this server.</summary>
    public DateTimeOffset? EulaAcceptedAt { get; set; }

    public string? EulaAcceptedBy { get; set; }

    /// <summary>"host" or "host:port" as Minecraft expects it.</summary>
    public string ConnectionString => Port == 25565 ? Address : $"{Address}:{Port}";
}
