using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Textures;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Builds;

/// <summary>Admin operations on game servers.</summary>
public sealed class ServerService(IDbContextFactory<LumoraDbContext> dbFactory)
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
        return null;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Servers.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Converts an uploaded picture to the 64×64 base64 PNG Minecraft uses.</summary>
    public static string MakeIcon(byte[] picture) => Convert.ToBase64String(ServerIcon.Process(picture));
}
