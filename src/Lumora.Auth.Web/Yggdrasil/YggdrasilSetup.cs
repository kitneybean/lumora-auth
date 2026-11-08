using System.Threading.RateLimiting;
using Lumora.Auth.Core.Yggdrasil;
using Microsoft.AspNetCore.RateLimiting;

namespace Lumora.Auth.Web.Yggdrasil;

public static class YggdrasilSetup
{
    /// <summary>Header that lets launchers find the API from the site URL (authlib-injector "ALI").</summary>
    public const string ApiLocationHeader = "X-Authlib-Injector-API-Location";

    public static IServiceCollection AddYggdrasil(this IServiceCollection services, IConfiguration config)
    {
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LumoraPaths>();
        services.AddSingleton(sp => SigningKey.LoadOrCreate(sp.GetRequiredService<LumoraPaths>().SigningKeyFile));
        services.AddSingleton<JoinStore>();
        services.AddSingleton<ProfileWriter>();
        services.AddScoped<TokenService>();
        // Rate limits (including the Yggdrasil login policy) live in Security/RateLimits.cs.
        return services;
    }

    public static IApplicationBuilder UseYggdrasilApiLocation(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.Headers[ApiLocationHeader] = YggdrasilEndpoints.Prefix + "/";
            return next(context);
        });

    /// <summary>Serves uploaded skins and capes: <c>/textures/{sha256}</c>.</summary>
    public static IEndpointRouteBuilder MapTextures(this IEndpointRouteBuilder app)
    {
        app.MapGet("/textures/{hash:regex(^[0-9a-f]{{64}}$)}", (string hash, LumoraPaths paths, HttpContext http) =>
        {
            var file = paths.TextureFile(hash);
            if (!File.Exists(file))
                return Results.NotFound();
            // Content-addressed: the file behind a hash never changes.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(file, "image/png");
        });
        return app;
    }
}
