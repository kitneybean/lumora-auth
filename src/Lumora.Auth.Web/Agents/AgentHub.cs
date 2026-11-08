using Lumora.Auth.Web.Security;
using Lumora.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Lumora.Auth.Web.Agents;

/// <summary>
/// The agent's only connection to the site (/agent/hub). Agents report here; the site calls agent methods
/// through <see cref="AgentConnectionManager"/>. Only reports about servers assigned to the agent are accepted.
/// </summary>
[Authorize(Policy = Policies.Agent)]
public sealed class AgentHub(AgentConnectionManager connections, AgentService agents, ILogger<AgentHub> logger) : Hub
{
    private Guid AgentId => AgentAuthenticationHandler.AgentIdOf(Context.User!);

    public override Task OnConnectedAsync()
    {
        connections.Connected(AgentId, Context);
        logger.LogInformation("Agent {AgentId} connected", AgentId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Disconnected(AgentId, Context.ConnectionId);
        logger.LogInformation("Agent {AgentId} disconnected", AgentId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManagedServerConfigDto>> Hello(AgentMachineDto machine)
    {
        var ip = ClientIp.Of(Context.GetHttpContext()!)?.ToString();
        if (connections.Session(AgentId) is { } s)
            s.Machine = machine;
        return agents.HelloAsync(AgentId, machine, ip, Context.ConnectionAborted);
    }

    public async Task Heartbeat(AgentHeartbeatDto beat)
    {
        var before = connections.Session(AgentId)?.LastHeartbeat;
        connections.Heartbeat(AgentId, beat);
        if (before is null || DateTimeOffset.UtcNow - before > TimeSpan.FromMinutes(1) || DateTimeOffset.UtcNow.Second < 10)
            await agents.TouchAsync(AgentId, Context.ConnectionAborted);
    }

    public void Status(ServerStatusReportDto report)
    {
        if (connections.Owns(AgentId, report.ServerId))
            connections.UpdateStatus(report);
    }

    public void Console(ConsoleLinesDto lines)
    {
        if (connections.Owns(AgentId, lines.ServerId))
            connections.AppendConsole(lines.ServerId, lines.Lines.Take(500).ToList());
    }
}
