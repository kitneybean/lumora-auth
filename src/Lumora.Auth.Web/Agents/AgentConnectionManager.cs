using System.Collections.Concurrent;
using Lumora.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace Lumora.Auth.Web.Agents;

/// <summary>
/// Live view of connected agents (singleton): which agent is online, the last status of every managed server
/// and the recent console output. Admin pages subscribe to the events; commands go to agents through here.
/// </summary>
public sealed class AgentConnectionManager(IHubContext<AgentHub> hub, ILogger<AgentConnectionManager> logger)
{
    public const int ConsoleCapacity = 2000;

    public sealed class AgentSession
    {
        public required string ConnectionId { get; init; }
        public required HubCallerContext Context { get; init; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        public AgentMachineDto? Machine { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; } = DateTimeOffset.UtcNow;
        public double CpuPercent { get; set; }
        public long MemoryUsedMb { get; set; }
        public HashSet<Guid> ServerIds { get; set; } = [];
    }

    private sealed class ServerLive
    {
        public ServerStatusReportDto? Status;
        public DateTimeOffset UpdatedAt;
        public readonly LinkedList<string> Console = new();
    }

    private readonly ConcurrentDictionary<Guid, AgentSession> _agents = new();
    private readonly ConcurrentDictionary<Guid, ServerLive> _servers = new();

    /// <summary>An agent connected, disconnected or sent a heartbeat.</summary>
    public event Action<Guid>? AgentChanged;

    /// <summary>A server's status changed.</summary>
    public event Action<Guid>? ServerChanged;

    /// <summary>New console lines of a server.</summary>
    public event Action<Guid, IReadOnlyList<string>>? ConsoleAppended;

    public void Connected(Guid agentId, HubCallerContext context)
    {
        var session = new AgentSession { ConnectionId = context.ConnectionId, Context = context };
        if (_agents.TryGetValue(agentId, out var old) && old.ConnectionId != context.ConnectionId)
        {
            // The same key connected twice (e.g. a copied agent.json): keep the newest connection.
            logger.LogWarning("Agent {AgentId} connected again, closing the previous connection", agentId);
            old.Context.Abort();
        }
        _agents[agentId] = session;
        Raise(AgentChanged, agentId);
    }

    public void Disconnected(Guid agentId, string connectionId)
    {
        if (_agents.TryGetValue(agentId, out var s) && s.ConnectionId == connectionId)
        {
            _agents.TryRemove(agentId, out _);
            foreach (var id in s.ServerIds)
                Raise(ServerChanged, id);
        }
        Raise(AgentChanged, agentId);
    }

    /// <summary>Closes the agent's connection (key revoked).</summary>
    public void Disconnect(Guid agentId)
    {
        if (_agents.TryRemove(agentId, out var s))
            s.Context.Abort();
        Raise(AgentChanged, agentId);
    }

    public bool IsOnline(Guid agentId) => _agents.ContainsKey(agentId);

    public AgentSession? Session(Guid agentId) => _agents.GetValueOrDefault(agentId);

    public void SetServers(Guid agentId, IEnumerable<Guid> serverIds)
    {
        if (_agents.TryGetValue(agentId, out var s))
            s.ServerIds = serverIds.ToHashSet();
    }

    public bool Owns(Guid agentId, Guid serverId) =>
        _agents.TryGetValue(agentId, out var s) && s.ServerIds.Contains(serverId);

    public void Heartbeat(Guid agentId, AgentHeartbeatDto beat)
    {
        if (!_agents.TryGetValue(agentId, out var s))
            return;
        s.Machine = beat.Machine;
        s.LastHeartbeat = DateTimeOffset.UtcNow;
        s.CpuPercent = beat.CpuPercent;
        s.MemoryUsedMb = beat.MemoryUsedMb;
        foreach (var report in beat.Servers)
            if (s.ServerIds.Contains(report.ServerId))
                UpdateStatus(report);
        Raise(AgentChanged, agentId);
    }

    public void UpdateStatus(ServerStatusReportDto report)
    {
        var live = _servers.GetOrAdd(report.ServerId, _ => new ServerLive());
        var changed = live.Status is null || !SameStatus(live.Status, report);
        live.Status = report;
        live.UpdatedAt = DateTimeOffset.UtcNow;
        if (changed)
            Raise(ServerChanged, report.ServerId);
    }

    /// <summary>Last status of a server, or null when its agent is offline or never reported.</summary>
    public ServerStatusReportDto? Status(Guid serverId, Guid? agentId)
    {
        if (agentId is null || !IsOnline(agentId.Value))
            return null;
        return _servers.TryGetValue(serverId, out var live) ? live.Status : null;
    }

    public void AppendConsole(Guid serverId, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;
        var live = _servers.GetOrAdd(serverId, _ => new ServerLive());
        lock (live.Console)
        {
            foreach (var line in lines)
            {
                live.Console.AddLast(line.Length > 4000 ? line[..4000] : line);
                if (live.Console.Count > ConsoleCapacity)
                    live.Console.RemoveFirst();
            }
        }
        var handler = ConsoleAppended;
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<Guid, IReadOnlyList<string>>)d)(serverId, lines); }
            catch (Exception e) { logger.LogWarning(e, "Console subscriber failed"); }
        }
    }

    public IReadOnlyList<string> Console(Guid serverId)
    {
        if (!_servers.TryGetValue(serverId, out var live))
            return [];
        lock (live.Console)
            return live.Console.ToList();
    }

    /// <summary>Replaces the console buffer with what the agent has (after reconnecting or opening the page).</summary>
    public void ResetConsole(Guid serverId, IReadOnlyList<string> lines)
    {
        var live = _servers.GetOrAdd(serverId, _ => new ServerLive());
        lock (live.Console)
        {
            live.Console.Clear();
            foreach (var line in lines.TakeLast(ConsoleCapacity))
                live.Console.AddLast(line);
        }
    }

    /// <summary>Calls a method on the agent and waits for its answer.</summary>
    public async Task<T> InvokeAsync<T>(Guid agentId, string method, object?[] args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentId, out var s))
            throw new AgentOfflineException();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        return await hub.Clients.Client(s.ConnectionId).InvokeCoreAsync<T>(method, args, cts.Token);
    }

    private static bool SameStatus(ServerStatusReportDto a, ServerStatusReportDto b) =>
        a.State == b.State && a.Activity == b.Activity && a.ProgressPercent == b.ProgressPercent
        && a.InstalledRevision == b.InstalledRevision && a.Players.SequenceEqual(b.Players)
        && a.LastError == b.LastError && Math.Abs(a.CpuPercent - b.CpuPercent) < 0.5 && a.MemoryMb == b.MemoryMb;

    private void Raise(Action<Guid>? handler, Guid id)
    {
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<Guid>)d)(id); }
            catch (Exception e) { logger.LogWarning(e, "Agent event subscriber failed"); }
        }
    }
}

public sealed class AgentOfflineException() : Exception("Агент не в сети.");
