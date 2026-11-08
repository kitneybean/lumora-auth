using System.Text.RegularExpressions;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Lumora.Auth.Web.Security;
using Lumora.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Agents;

/// <summary>Agents in the database: join tokens, registration, revocation, server configs and admin commands.</summary>
public sealed partial class AgentService(
    IDbContextFactory<LumoraDbContext> dbFactory,
    AgentConnectionManager connections,
    AuditService audit,
    TimeProvider time,
    ILogger<AgentService> logger)
{
    public static readonly TimeSpan JoinTokenLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Keys the site always sets in server.properties itself.</summary>
    public static readonly IReadOnlySet<string> ManagedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "server-port", "online-mode", "enforce-secure-profile",
    };

    // ---------- Join tokens and registration ----------

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateJoinTokenAsync(string? name, string? createdBy, CancellationToken ct = default)
    {
        var token = AgentKeys.NewJoinToken();
        var expires = time.GetUtcNow() + JoinTokenLifetime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AgentJoinTokens.Add(new AgentJoinToken
        {
            TokenHash = AgentKeys.Hash(token),
            Name = CleanName(name),
            ExpiresAt = expires,
            CreatedBy = createdBy,
        });
        // Old tokens are of no use to anyone.
        var stale = time.GetUtcNow() - TimeSpan.FromDays(7);
        await db.AgentJoinTokens.Where(t => t.ExpiresAt < stale).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return (token, expires);
    }

    /// <summary>Exchanges a join token for a new agent and its key; null when the token is unknown, used or expired.</summary>
    public async Task<AgentRegisterResponse?> RegisterAsync(AgentRegisterRequest request, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || !request.Token.StartsWith(AgentKeys.TokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = AgentKeys.Hash(request.Token.Trim());
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = await db.AgentJoinTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
            return null;

        var key = AgentKeys.NewKey();
        var m = request.Machine;
        var agent = new Agent
        {
            Name = CleanName(token.Name) ?? CleanName(request.Name) ?? CleanName(m.Hostname) ?? "agent",
            KeyHash = AgentKeys.Hash(key),
            CreatedBy = token.CreatedBy,
            LastSeenAt = now,
            LastIp = ip,
        };
        Apply(agent, m);
        db.Agents.Add(agent);
        token.UsedAt = now;
        token.AgentId = agent.Id;
        await db.SaveChangesAsync(ct);
        audit.Log("agent.register", true, null, token.CreatedBy, ip, $"агент «{agent.Name}» ({m.Hostname}, {m.Os})");
        return new AgentRegisterResponse(agent.Id, agent.Name, key);
    }

    // ---------- Agents ----------

    public async Task<List<Agent>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Agents.AsNoTracking().Include(a => a.Servers).OrderBy(a => a.IsRevoked).ThenBy(a => a.Name).ToListAsync(ct);
    }

    public async Task RevokeAsync(Guid agentId, string? by, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null || agent.IsRevoked)
            return;
        agent.IsRevoked = true;
        agent.RevokedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        connections.Disconnect(agentId);
        audit.Log("admin.agent.revoke", true, null, by, null, $"агент «{agent.Name}»");
    }

    /// <summary>Removes a revoked agent; its servers stay on the site without an agent.</summary>
    public async Task DeleteAsync(Guid agentId, string? by, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Servers).FirstOrDefaultAsync(a => a.Id == agentId && a.IsRevoked, ct);
        if (agent is null)
            return;
        foreach (var s in agent.Servers)
            s.AgentId = null;
        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
        audit.Log("admin.agent.delete", true, null, by, null, $"агент «{agent.Name}»");
    }

    public async Task RenameAsync(Guid agentId, string name, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        if (clean is null)
            return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Name, clean), ct);
    }

    /// <summary>Agent said hello: remember its machine and return the servers it must run.</summary>
    public async Task<IReadOnlyList<ManagedServerConfigDto>> HelloAsync(Guid agentId, AgentMachineDto machine, string? ip, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is null)
            return [];
        Apply(agent, machine);
        agent.LastSeenAt = time.GetUtcNow();
        agent.LastIp = ip;
        await db.SaveChangesAsync(ct);

        var servers = await db.Servers.AsNoTracking().Include(s => s.GameBuild)
            .Where(s => s.AgentId == agentId).ToListAsync(ct);
        connections.SetServers(agentId, servers.Select(s => s.Id));
        return servers.Select(ConfigOf).ToList();
    }

    public async Task TouchAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, now), ct);
    }

    private static void Apply(Agent agent, AgentMachineDto m)
    {
        agent.Version = Trim(m.AgentVersion, 32);
        agent.Os = Trim($"{m.Os} {m.Architecture}", 128);
        agent.Hostname = Trim(m.Hostname, 128);
        agent.CpuCount = Math.Clamp(m.CpuCount, 0, 4096);
        agent.MemoryMb = Math.Max(0, m.MemoryMb);
    }

    // ---------- Server configuration ----------

    public static ManagedServerConfigDto ConfigOf(GameServer s) => new(
        s.Id,
        s.Name,
        s.Port,
        s.MemoryMb,
        SplitLines(s.ExtraJvmArgs),
        s.AutoStart,
        s.AutoRestart,
        ParseTimes(s.RestartTimes, out _),
        ParseTimes(s.BackupTimes, out _),
        s.BackupsToKeep,
        s.EulaAcceptedAt is not null,
        s.GameBuild is { IsPublished: true } ? s.GameBuildId : null,
        s.GameBuild is { IsPublished: true } b ? b.Revision : 0,
        ParseProperties(s.ServerProperties, out _));

    /// <summary>Sends the current config of a server to its agent (if online). Also tells the old agent to forget it.</summary>
    public async Task PushConfigAsync(Guid serverId, Guid? previousAgentId = null, CancellationToken ct = default)
    {
        try
        {
            await PushConfigCoreAsync(serverId, previousAgentId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Called fire-and-forget after saves: the agent gets the config again on its next connect anyway.
            logger.LogWarning(e, "Could not send the config of server {ServerId} to its agent", serverId);
        }
    }

    private async Task PushConfigCoreAsync(Guid serverId, Guid? previousAgentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var server = await db.Servers.AsNoTracking().Include(s => s.GameBuild).FirstOrDefaultAsync(s => s.Id == serverId, ct);

        if (previousAgentId is { } old && old != server?.AgentId && connections.IsOnline(old))
        {
            await SafeInvokeAsync(old, AgentMethods.RemoveServer, [serverId, false], ct);
            await RefreshOwnershipAsync(db, old, ct);
        }
        if (server?.AgentId is not { } agentId || !connections.IsOnline(agentId))
            return;
        await RefreshOwnershipAsync(db, agentId, ct);
        await SafeInvokeAsync(agentId, AgentMethods.ApplyConfig, [ConfigOf(server)], ct);
    }

    /// <summary>A build was published: agents running servers on it get the new revision.</summary>
    public async Task PushBuildAsync(Guid buildId, CancellationToken ct = default)
    {
        List<Guid> ids;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            ids = await db.Servers.Where(s => s.GameBuildId == buildId && s.AgentId != null).Select(s => s.Id).ToListAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not notify agents about build {BuildId}", buildId);
            return;
        }
        foreach (var id in ids)
            await PushConfigAsync(id, null, ct);
    }

    private async Task RefreshOwnershipAsync(LumoraDbContext db, Guid agentId, CancellationToken ct) =>
        connections.SetServers(agentId, await db.Servers.Where(s => s.AgentId == agentId).Select(s => s.Id).ToListAsync(ct));

    private async Task SafeInvokeAsync(Guid agentId, string method, object?[] args, CancellationToken ct)
    {
        try
        {
            var result = await connections.InvokeAsync<AgentResult>(agentId, method, args, ct: ct);
            if (!result.Ok)
                logger.LogWarning("Agent {AgentId} rejected {Method}: {Error}", agentId, method, result.Error);
        }
        catch (Exception e) when (e is AgentOfflineException or OperationCanceledException or HubException or IOException)
        {
            logger.LogWarning("Agent {AgentId} did not answer {Method}: {Message}", agentId, method, e.Message);
        }
    }

    // ---------- Admin commands ----------

    /// <summary>Runs a command on the server's agent and writes it to the security journal.</summary>
    public async Task<AgentResult> CommandAsync(Guid serverId, string method, object?[] args, string? actor, string? details = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var (agentId, name) = await AgentOfAsync(serverId, ct);
        AgentResult result;
        if (agentId is null)
            result = AgentResult.Fail("Сервер не привязан к агенту.");
        else
        {
            try
            {
                result = await connections.InvokeAsync<AgentResult>(agentId.Value, method, args, timeout, ct);
            }
            catch (Exception e) when (e is AgentOfflineException or OperationCanceledException or HubException or IOException)
            {
                result = AgentResult.Fail(e is OperationCanceledException ? "Агент не ответил вовремя." : e is AgentOfflineException ? e.Message : "Агент не смог выполнить команду: " + e.Message);
            }
        }
        audit.Log($"admin.server.{method.ToLowerInvariant()}", result.Ok, null, actor, null,
            Trim($"сервер «{name}»" + (details is null ? "" : ": " + details) + (result.Ok ? "" : " — " + result.Error), 2000));
        return result;
    }

    /// <summary>Asks the agent for data (files, backups, console); no journal entry.</summary>
    public async Task<T?> QueryAsync<T>(Guid serverId, string method, object?[] args, CancellationToken ct = default)
    {
        var (agentId, _) = await AgentOfAsync(serverId, ct);
        if (agentId is null)
            return default;
        try
        {
            return await connections.InvokeAsync<T>(agentId.Value, method, args, ct: ct);
        }
        catch (Exception e) when (e is AgentOfflineException or OperationCanceledException or HubException or IOException)
        {
            logger.LogInformation("Query {Method} to agent {AgentId} failed: {Message}", method, agentId, e.Message);
            return default;
        }
    }

    private async Task<(Guid? AgentId, string Name)> AgentOfAsync(Guid serverId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Servers.AsNoTracking().Where(x => x.Id == serverId).Select(x => new { x.AgentId, x.Name }).FirstOrDefaultAsync(ct);
        return (s?.AgentId, s?.Name ?? serverId.ToString());
    }

    // ---------- Parsing helpers (also used to validate admin input) ----------

    [GeneratedRegex(@"^([01]?\d|2[0-3]):([0-5]\d)$")]
    private static partial Regex TimeRegex();

    /// <summary>"4:00, 16:30" → ["04:00", "16:30"]; error names the first bad entry.</summary>
    public static IReadOnlyList<string> ParseTimes(string? text, out string? error)
    {
        error = null;
        var result = new List<string>();
        foreach (var part in (text ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = TimeRegex().Match(part);
            if (!m.Success)
            {
                error ??= $"«{part}» — не время в формате ЧЧ:ММ.";
                continue;
            }
            var t = $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}";
            if (!result.Contains(t))
                result.Add(t);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]{0,63}$")]
    private static partial Regex PropertyKeyRegex();

    /// <summary>"key=value" per line (# comments allowed) → dictionary; managed keys are rejected.</summary>
    public static IReadOnlyDictionary<string, string> ParseProperties(string? text, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            var key = eq < 0 ? line : line[..eq].Trim();
            var value = eq < 0 ? "" : line[(eq + 1)..].Trim();
            if (eq < 0 || !PropertyKeyRegex().IsMatch(key) || value.Length > 1000 || value.Contains('\r'))
            {
                error ??= $"Строка «{Trim(line, 60)}» — нужен формат ключ=значение.";
                continue;
            }
            if (ManagedProperties.Contains(key))
            {
                error ??= $"Ключ {key} сайт задаёт сам.";
                continue;
            }
            result[key] = value;
        }
        return result;
    }

    public static IReadOnlyList<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? CleanName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : Trim(clean, 64);
    }

    private static string Trim(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];
}
