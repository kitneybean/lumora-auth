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

    /// <summary>"host" or "host:port" as Minecraft expects it.</summary>
    public string ConnectionString => Port == 25565 ? Address : $"{Address}:{Port}";
}
