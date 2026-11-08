using System.Text.Json;
using Lumora.Auth.Data;
using Lumora.Auth.Web.Builds;
using Lumora.Auth.Web.Launcher;
using Lumora.Auth.Web.Security;
using Lumora.Auth.Web.Yggdrasil;
using Lumora.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Agents;

/// <summary>
/// HTTP side of the agent protocol: registration with a join token, server manifests and build files
/// (agent key required), the install script and the SignalR hub.
/// </summary>
public static class AgentEndpoints
{
    public const string HubPath = "/agent/hub";
    public const string Prefix = "/api/agent";

    public static IServiceCollection AddLumoraAgents(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.SchemeName, null);
        services.AddSingleton<AgentConnectionManager>();
        services.AddSingleton<AgentService>();
        services.AddSignalR().AddHubOptions<AgentHub>(o =>
        {
            o.MaximumReceiveMessageSize = 1 << 20;
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            o.EnableDetailedErrors = false;
        });
        return services;
    }

    public static IEndpointRouteBuilder MapLumoraAgents(this IEndpointRouteBuilder app)
    {
        app.MapHub<AgentHub>(HubPath);
        app.MapGet("/agent/install.sh", InstallScript).AllowAnonymous();

        var api = app.MapGroup(Prefix).DisableAntiforgery().WithTags("Agent");
        api.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(YggdrasilEndpoints.AuthRateLimitPolicy);

        var secured = api.MapGroup("").RequireAuthorization(Policies.Agent);
        secured.MapGet("/servers/{id:guid}/manifest", Manifest);
        secured.MapGet("/files/{sha1}", File);
        return app;
    }

    private static async Task<IResult> Register(AgentRegisterRequest request, AgentService agents, AutoBanService autoBan, HttpContext http)
    {
        var ip = ClientIp.Of(http);
        var result = await agents.RegisterAsync(request, ip?.ToString(), http.RequestAborted);
        if (result is null)
        {
            autoBan.Record(ip, AutoBanService.Offence.FailedLogin);
            return Results.Json(new { error = "Токен не подходит: он неверный, уже использован или истёк. Создайте новый в админке." },
                statusCode: StatusCodes.Status403Forbidden);
        }
        return Results.Json(result, LumoraJson.Options);
    }

    private static async Task<IResult> Manifest(Guid id, HttpContext http, LumoraDbContext db, AuthlibInjectorProvider authlib,
        IOptions<LumoraOptions> options, CancellationToken ct)
    {
        var agentId = AgentAuthenticationHandler.AgentIdOf(http.User);
        var server = await db.Servers.AsNoTracking().Include(s => s.GameBuild).ThenInclude(b => b!.Files)
            .FirstOrDefaultAsync(s => s.Id == id && s.AgentId == agentId, ct);
        if (server is null)
            return Results.NotFound();
        if (server.GameBuild is not { IsPublished: true } build)
            return Results.Json(new { error = "У сервера нет опубликованной сборки." }, statusCode: StatusCodes.Status409Conflict);

        // The snapshot from the last publish; builds published before agents existed are read live.
        var manifest = build.PublishedServerManifestJson is { } json
            ? JsonSerializer.Deserialize<ServerManifestDto>(json, LumoraJson.Options)!
            : BuildService.ServerManifestOf(build);
        var url = options.Value.PublicUrl.TrimEnd('/');
        var injector = await authlib.GetAsync(ct);
        return Results.Json(manifest with
        {
            ServerId = server.Id,
            Files = manifest.Files.Select(f => f with { Url = $"{url}{Prefix}/files/{f.Sha1}" }).ToList(),
            AuthlibInjector = injector is null ? null
                : new AuthlibInjectorDto(injector.Version, $"{url}{LauncherEndpoints.Prefix}/authlib-injector/download", injector.Sha256),
            YggdrasilUrl = $"{url}{YggdrasilEndpoints.Prefix}",
        }, LumoraJson.Options);
    }

    private static async Task<IResult> File(string sha1, HttpContext http, LumoraDbContext db, BuildFileStore store, CancellationToken ct)
    {
        var agentId = AgentAuthenticationHandler.AgentIdOf(http.User);
        // Only files of builds that this agent's servers use.
        var allowed = await db.Servers.Where(s => s.AgentId == agentId && s.GameBuildId != null)
            .SelectMany(s => s.GameBuild!.Files).AnyAsync(f => f.Sha1 == sha1, ct);
        return allowed && store.Exists(sha1)
            ? Results.File(store.PathOf(sha1), "application/octet-stream", enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static string? _scriptTemplate;

    private static IResult InstallScript(IOptions<LumoraOptions> options)
    {
        _scriptTemplate ??= ReadTemplate();
        var script = _scriptTemplate
            .Replace("__SITE_URL__", options.Value.PublicUrl.TrimEnd('/'))
            .Replace("__AGENT_REPO__", options.Value.AgentRepository);
        return Results.Text(script, "text/x-shellscript; charset=utf-8");
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(AgentEndpoints).Assembly.GetManifestResourceStream("Lumora.Auth.Web.Agents.install-agent.sh")
            ?? throw new InvalidOperationException("install-agent.sh is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
