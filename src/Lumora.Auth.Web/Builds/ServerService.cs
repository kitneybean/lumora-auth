using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Textures;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Builds;

/// <summary>Admin operations on game servers.</summary>
public sealed class ServerService(IDbContextFactory<LumoraDbContext> dbFactory, Agents.AgentService agents)
{
    public async Task<List<GameServer>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers.AsNoTracking().Include(s => s.GameBuild)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>Creates or updates a server. Returns an error message or null.</summary>
    public async Task<string?> SaveAsync(GameServer edited, CancellationToken ct = default)
    {
        var name = edited.Name?.Trim() ?? "";
        var address = edited.Address?.Trim().ToLowerInvariant() ?? "";
        if (name.Length == 0)
            return "Укажите название сервера.";
        if (address.Length == 0 || address.Contains(' ') || address.Contains('/') || address.Contains(':'))
            return "Адрес — это домен или IP без порта, например play.example.com (порт задаётся отдельно).";
        if (edited.Port is < 1 or > 65535)
            return "Порт должен быть от 1 до 65535.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Servers.AnyAsync(s => s.Id != edited.Id && s.Address == address && s.Port == edited.Port, ct))
            return "Сервер с таким адресом и портом уже есть.";

        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == edited.Id, ct);
        if (server is null)
        {
            server = new GameServer { Id = edited.Id, Name = name, Address = address };
            db.Servers.Add(server);
        }
        server.Name = name;
        server.Description = string.IsNullOrWhiteSpace(edited.Description) ? null : edited.Description.Trim();
        server.Address = address;
        server.Port = edited.Port;
        server.GameBuildId = edited.GameBuildId;
        server.SortOrder = edited.SortOrder;
        server.AddToServerList = edited.AddToServerList;
        server.AutoConnect = edited.AutoConnect;
        server.IsEnabled = edited.IsEnabled;
        server.IconBase64 = edited.IconBase64;
        await db.SaveChangesAsync(ct);
        if (server.AgentId is not null)
            _ = agents.PushConfigAsync(server.Id);
        return null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agentId = await db.Servers.Where(s => s.Id == id).Select(s => s.AgentId).FirstOrDefaultAsync(ct);
        await db.Servers.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        if (agentId is not null)
            _ = agents.PushConfigAsync(id, agentId);
    }

    /// <summary>
    /// Saves how the agent runs the server. Accepting the EULA is recorded once with the admin name.
    /// Returns an error message or null.
    /// </summary>
    public async Task<string?> SaveManagementAsync(GameServer edited, bool acceptEula, string? actor, CancellationToken ct = default)
    {
        if (edited.MemoryMb is < 512 or > 262144)
            return "Память: от 512 МБ до 256 ГБ.";
        if (edited.BackupsToKeep is < 1 or > 100)
            return "Хранить бэкапов: от 1 до 100.";
        Agents.AgentService.ParseTimes(edited.RestartTimes, out var timeError);
        if (timeError is not null)
            return "Перезапуск: " + timeError;
        Agents.AgentService.ParseTimes(edited.BackupTimes, out timeError);
        if (timeError is not null)
            return "Бэкапы: " + timeError;
        Agents.AgentService.ParseProperties(edited.ServerProperties, out var propError);
        if (propError is not null)
            return "server.properties: " + propError;
        foreach (var arg in Agents.AgentService.SplitLines(edited.ExtraJvmArgs))
        {
            if (!arg.StartsWith('-') || arg.StartsWith("-javaagent", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("-Xmx", StringComparison.Ordinal) || arg.Contains('\0'))
                return $"Аргумент JVM «{arg}» не подходит: по одному в строке, начинается с «-»; -Xmx и -javaagent сайт задаёт сам.";
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == edited.Id, ct);
        if (server is null)
            return "Сервер не найден.";
        if (edited.AgentId is { } agentId && !await db.Agents.AnyAsync(a => a.Id == agentId && !a.IsRevoked, ct))
            return "Агент не найден или отозван.";

        var previousAgent = server.AgentId;
        server.AgentId = edited.AgentId;
        server.MemoryMb = edited.MemoryMb;
        server.ExtraJvmArgs = Blank(edited.ExtraJvmArgs);
        server.AutoStart = edited.AutoStart;
        server.AutoRestart = edited.AutoRestart;
        server.RestartTimes = Blank(string.Join(", ", Agents.AgentService.ParseTimes(edited.RestartTimes, out _)));
        server.BackupTimes = Blank(string.Join(", ", Agents.AgentService.ParseTimes(edited.BackupTimes, out _)));
        server.BackupsToKeep = edited.BackupsToKeep;
        server.ServerProperties = Blank(edited.ServerProperties);
        if (acceptEula && server.EulaAcceptedAt is null)
        {
            server.EulaAcceptedAt = DateTimeOffset.UtcNow;
            server.EulaAcceptedBy = actor;
        }
        await db.SaveChangesAsync(ct);
        _ = agents.PushConfigAsync(server.Id, previousAgent);
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Converts an uploaded picture to the 64×64 base64 PNG Minecraft uses.</summary>
    public static string MakeIcon(byte[] picture) => Convert.ToBase64String(ServerIcon.Process(picture));
}
