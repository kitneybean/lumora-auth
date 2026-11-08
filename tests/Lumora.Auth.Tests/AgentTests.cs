using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Lumora.Auth.Tests.Infrastructure;
using Lumora.Auth.Web.Agents;
using Lumora.Auth.Web.Builds;
using Lumora.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumora.Auth.Tests;

/// <summary>Site side of the agent protocol, driven by a fake agent over a real SignalR connection.</summary>
[Collection(AppCollection.Name)]
public class AgentTests(LumoraAppFactory app)
{
    private static readonly AgentMachineDto Machine = new("0.1.0", "Linux", "x64", "test-host", 8, 16384, 100_000, 200_000);

    private AgentService Agents => app.Services.GetRequiredService<AgentService>();
    private AgentConnectionManager Connections => app.Services.GetRequiredService<AgentConnectionManager>();

    [Fact]
    public async Task Join_token_works_once_and_bad_tokens_are_refused()
    {
        var (token, _) = await Agents.CreateJoinTokenAsync("Node A", "admin");
        var http = app.CreateClient();

        var ok = await http.PostAsJsonAsync("/api/agent/register", new AgentRegisterRequest(token, null, Machine), LumoraJson.Options);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var registered = (await ok.Content.ReadFromJsonAsync<AgentRegisterResponse>(LumoraJson.Options))!;
        Assert.Equal("Node A", registered.Name);
        Assert.StartsWith(AgentKeys.KeyPrefix, registered.Key);

        // The key is stored only as a hash.
        await app.WithDbAsync(async db =>
        {
            var agent = await db.Agents.SingleAsync(a => a.Id == registered.AgentId);
            Assert.NotEqual(registered.Key, agent.KeyHash);
            Assert.Equal(AgentKeys.Hash(registered.Key), agent.KeyHash);
            Assert.Equal("test-host", agent.Hostname);
        });

        var again = await http.PostAsJsonAsync("/api/agent/register", new AgentRegisterRequest(token, null, Machine), LumoraJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        var junk = await http.PostAsJsonAsync("/api/agent/register", new AgentRegisterRequest("lmj_nope", null, Machine), LumoraJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, junk.StatusCode);
    }

    [Fact]
    public async Task Agent_gets_its_servers_answers_commands_and_reports_status()
    {
        var (agentId, key) = await RegisterAsync("Node B");
        var serverId = await CreateManagedServerAsync("Agent Realm", agentId, 25601);
        var otherId = await CreateManagedServerAsync("Someone Else", null, 25602);

        var started = new TaskCompletionSource<Guid>();
        await using var hub = Connect(key);
        hub.On<Guid, AgentResult>(AgentMethods.Start, id =>
        {
            started.TrySetResult(id);
            return AgentResult.Success;
        });
        hub.On<Guid, string, AgentResult>(AgentMethods.SendCommand, (_, cmd) =>
            cmd == "list" ? AgentResult.Success : AgentResult.Fail("unknown"));
        await hub.StartAsync();

        var configs = await hub.InvokeAsync<List<ManagedServerConfigDto>>(AgentHubMethods.Hello, Machine);
        var config = Assert.Single(configs);
        Assert.Equal(serverId, config.ServerId);
        Assert.Equal(25601, config.Port);
        Assert.False(config.EulaAccepted);
        Assert.True(Connections.IsOnline(agentId));

        // Admin command → agent → result, written to the journal.
        var result = await Agents.CommandAsync(serverId, AgentMethods.Start, [serverId], "admin");
        Assert.True(result.Ok, result.Error);
        Assert.Equal(serverId, await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var failed = await Agents.CommandAsync(serverId, AgentMethods.SendCommand, [serverId, "oops"], "admin", "oops");
        Assert.False(failed.Ok);

        // Reports about own servers are accepted, about others ignored.
        await hub.InvokeAsync(AgentHubMethods.Status, Report(serverId, ManagedServerState.Running, "Steve"));
        await hub.InvokeAsync(AgentHubMethods.Status, Report(otherId, ManagedServerState.Running, "Intruder"));
        await hub.InvokeAsync(AgentHubMethods.Console, new ConsoleLinesDto(serverId, ["[Server thread/INFO]: Done (3.2s)!"]));
        Assert.Equal(["Steve"], Connections.Status(serverId, agentId)!.Players);
        Assert.Null(Connections.Status(otherId, agentId));
        Assert.Contains(Connections.Console(serverId), l => l.Contains("Done"));

        await app.WithDbAsync(async db =>
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "admin.server.start" && e.Success && e.Details!.Contains("Agent Realm"))));
    }

    [Fact]
    public async Task Server_manifest_contains_server_files_and_files_are_limited_to_own_builds()
    {
        var (agentId, key) = await RegisterAsync("Node C");
        var (_, otherKey) = await RegisterAsync("Node D");
        var buildId = await WithBuilds(async b =>
        {
            var id = await b.CreateAsync("Agent pack", "1.21.1", LoaderType.NeoForge, "21.1.77");
            await b.AddFileAsync(id, "mods/common.jar", Bytes("common"));
            await b.AddFileAsync(id, "mods/server.jar", Bytes("server side"));
            await b.AddFileAsync(id, "mods/client.jar", Bytes("client side"));
            var build = (await b.GetAsync(id))!;
            await b.UpdateFileAsync(build.Files.Single(f => f.Path == "mods/server.jar").Id, FileSide.Server, FileInstallMode.Required, null);
            await b.UpdateFileAsync(build.Files.Single(f => f.Path == "mods/client.jar").Id, FileSide.Client, FileInstallMode.Required, null);
            await b.PublishAsync(id);
            return id;
        });
        var serverId = await CreateManagedServerAsync("Pack Realm", agentId, 25603, buildId);

        var http = AgentClient(key);
        var manifest = (await http.GetFromJsonAsync<ServerManifestDto>($"/api/agent/servers/{serverId}/manifest", LumoraJson.Options))!;
        Assert.Equal(["mods/common.jar", "mods/server.jar"], manifest.Files.Select(f => f.Path).ToArray());
        Assert.Equal($"{LumoraAppFactory.PublicUrl}/api/yggdrasil", manifest.YggdrasilUrl);
        var serverJar = manifest.Files.Single(f => f.Path == "mods/server.jar");
        Assert.Equal("server side", await http.GetStringAsync(new Uri(serverJar.Url).PathAndQuery));

        // Another agent sees neither the server nor its files; no key, no access.
        var other = AgentClient(otherKey);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/agent/servers/{serverId}/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(new Uri(serverJar.Url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync($"/api/agent/servers/{serverId}/manifest")).StatusCode);
    }

    [Fact]
    public async Task Revoked_agent_is_disconnected_and_locked_out()
    {
        var (agentId, key) = await RegisterAsync("Node E");
        await using var hub = Connect(key);
        var closed = new TaskCompletionSource();
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        await hub.InvokeAsync<List<ManagedServerConfigDto>>(AgentHubMethods.Hello, Machine);

        await Agents.RevokeAsync(agentId, "admin");
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(Connections.IsOnline(agentId));

        await using var again = Connect(key);
        await Assert.ThrowsAnyAsync<Exception>(() => again.StartAsync());
    }

    [Fact]
    public async Task Install_script_points_to_this_site()
    {
        var script = await app.CreateClient().GetStringAsync("/agent/install.sh");
        Assert.StartsWith("#!/usr/bin/env bash", script);
        Assert.Contains($"SITE_URL=\"{LumoraAppFactory.PublicUrl}\"", script);
        Assert.DoesNotContain("\r", script);
    }

    [Theory]
    [InlineData("4:00, 16:30", new[] { "04:00", "16:30" }, false)]
    [InlineData("25:00", new string[0], true)]
    [InlineData("", new string[0], false)]
    public void Times_are_parsed(string text, string[] expected, bool hasError)
    {
        Assert.Equal(expected, AgentService.ParseTimes(text, out var error));
        Assert.Equal(hasError, error is not null);
    }

    [Fact]
    public void Managed_properties_cannot_be_overridden()
    {
        var props = AgentService.ParseProperties("# comment\nmotd=Hello\nmax-players = 50\nserver-port=1", out var error);
        Assert.Equal("Hello", props["motd"]);
        Assert.Equal("50", props["max-players"]);
        Assert.False(props.ContainsKey("server-port"));
        Assert.NotNull(error);
    }

    // ------------------------------------------------------------------

    private async Task<(Guid AgentId, string Key)> RegisterAsync(string name)
    {
        var (token, _) = await Agents.CreateJoinTokenAsync(name, "admin");
        var r = await Agents.RegisterAsync(new AgentRegisterRequest(token, null, Machine), null);
        return (r!.AgentId, r.Key);
    }

    private async Task<Guid> CreateManagedServerAsync(string name, Guid? agentId, int port, Guid? buildId = null)
    {
        var server = new GameServer { Name = name, Address = "agent.test", Port = port, GameBuildId = buildId, AgentId = agentId };
        await app.WithDbAsync(async db =>
        {
            db.Servers.Add(server);
            await db.SaveChangesAsync();
        });
        return server.Id;
    }

    private HubConnection Connect(string key) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, AgentEndpoints.HubPath), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(key);
            })
            .Build();

    private HttpClient AgentClient(string key)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static ServerStatusReportDto Report(Guid id, ManagedServerState state, params string[] players) =>
        new(id, state, null, null, 1, players, 12.5, 2048, 500, DateTimeOffset.UtcNow, null);

    private async Task<T> WithBuilds<T>(Func<BuildService, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<BuildService>());
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));
}
