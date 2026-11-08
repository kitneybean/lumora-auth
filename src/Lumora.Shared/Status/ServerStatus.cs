namespace Lumora.Shared.Status;

/// <summary>Result of a Server List Ping.</summary>
public sealed record ServerStatus(bool IsOnline, int Players, int MaxPlayers, int PingMs, string? Motd)
{
    public static ServerStatus Offline { get; } = new(false, 0, 0, 0, null);
}

public interface IServerStatusService
{
    Task<ServerStatus> GetStatusAsync(string address, int port, CancellationToken ct = default);
}
