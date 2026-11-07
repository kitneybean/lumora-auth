using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumora.Auth.Data;
using Lumora.Auth.Web.Builds;
using Lumora.Auth.Web.Yggdrasil;
using Lumora.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Launcher;

/// <summary>
/// API for Lumora Launcher: <c>/api/launcher/...</c>. Server list, build manifests and files need
/// the launcher access token (<c>Authorization: Bearer</c>); icons and authlib-injector are public.
/// </summary>
public static class LauncherEndpoints
{
    public const string Prefix = "/api/launcher";

    public static IEndpointRouteBuilder MapLauncherApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix).DisableAntiforgery().WithTags("Launcher");

        var secured = api.MapGroup("").AddEndpointFilter(RequireToken);
        secured.MapGet("/servers", Servers);
        secured.MapGet("/builds/{id:guid}/manifest", Manifest);
        secured.MapGet("/files/{sha1}", File);

        api.MapGet("/servers/{id:guid}/icon", Icon);
        api.MapGet("/authlib-injector", AuthlibInjector);
        api.MapGet("/authlib-injector/download", AuthlibInjectorDownload);
        return app;
    }

    private static string PublicUrl(IOptions<LumoraOptions> o) => o.Value.PublicUrl.TrimEnd('/');

    private static async Task<IResult> Servers(LumoraDbContext db, IOptions<LumoraOptions> options, CancellationToken ct)
    {
        var url = PublicUrl(options);
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.IsEnabled && s.GameBuild != null && s.GameBuild.IsPublished && s.GameBuild.PublishedManifestJson != null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.Description, s.Address, s.Port, s.IconBase64, s.AddToServerList, s.AutoConnect,
                Build = new { s.GameBuild!.Id, s.GameBuild.Name, s.GameBuild.Revision, s.GameBuild.MinecraftVersion, s.GameBuild.Loader, s.GameBuild.LoaderVersion },
            })
            .ToListAsync(ct);

        return Results.Json(servers.Select(s => new LauncherServerDto(
            s.Id, s.Name, s.Description, s.Address, s.Port,
            s.IconBase64 is null ? null : $"{url}{Prefix}/servers/{s.Id}/icon?v={IconVersion(s.IconBase64)}",
            s.AddToServerList, s.AutoConnect,
            new LauncherBuildSummaryDto(s.Build.Id, s.Build.Name, s.Build.Revision, s.Build.MinecraftVersion, s.Build.Loader, s.Build.LoaderVersion))).ToList(),
            LumoraJson.Options);
    }

    private static async Task<IResult> Manifest(Guid id, LumoraDbContext db, IOptions<LumoraOptions> options, CancellationToken ct)
    {
        var build = await db.Builds.AsNoTracking()
            .Where(b => b.Id == id && b.IsPublished)
            .Select(b => new { b.PublishedManifestJson })
            .FirstOrDefaultAsync(ct);
        if (build?.PublishedManifestJson is null)
            return Results.NotFound();

        var manifest = JsonSerializer.Deserialize<BuildManifestDto>(build.PublishedManifestJson, LumoraJson.Options)!;
        var url = PublicUrl(options);
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.GameBuildId == id && s.IsEnabled && s.AddToServerList)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(ct);

        return Results.Json(manifest with
        {
            Files = manifest.Files.Select(f => f with { Url = $"{url}{Prefix}/files/{f.Sha1}" }).ToList(),
            Servers = servers.Select(s => new LauncherServerEntryDto(s.Name, s.ConnectionString, s.IconBase64)).ToList(),
        }, LumoraJson.Options);
    }

    private static IResult File(string sha1, BuildFileStore store) =>
        store.Exists(sha1)
            ? Results.File(store.PathOf(sha1), "application/octet-stream", enableRangeProcessing: true)
            : Results.NotFound();

    private static async Task<IResult> Icon(Guid id, LumoraDbContext db, HttpContext http, CancellationToken ct)
    {
        var icon = await db.Servers.AsNoTracking().Where(s => s.Id == id).Select(s => s.IconBase64).FirstOrDefaultAsync(ct);
        if (icon is null)
            return Results.NotFound();
        http.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.Bytes(Convert.FromBase64String(icon), "image/png");
    }

    private static async Task<IResult> AuthlibInjector(AuthlibInjectorProvider provider, IOptions<LumoraOptions> options, CancellationToken ct)
    {
        var info = await provider.GetAsync(ct);
        return info is null
            ? Results.Problem("authlib-injector is not available yet", statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(new AuthlibInjectorDto(info.Version, $"{PublicUrl(options)}{Prefix}/authlib-injector/download", info.Sha256), LumoraJson.Options);
    }

    private static async Task<IResult> AuthlibInjectorDownload(AuthlibInjectorProvider provider, CancellationToken ct)
    {
        var info = await provider.GetAsync(ct);
        return info is null
            ? Results.Problem("authlib-injector is not available yet", statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.File(provider.JarPath, "application/java-archive", $"authlib-injector-{info.Version}.jar");
    }

    private static string IconVersion(string base64) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(base64)))[..8];

    private static async ValueTask<object?> RequireToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var tokens = http.RequestServices.GetRequiredService<TokenService>();
        if (await tokens.FindUsableAsync(token, null, http.RequestAborted) is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.Json(new ErrorResponse("UnauthorizedOperationException", "Invalid token."),
                YggdrasilJson.Options, statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(context);
    }
}
