using System.Net;
using System.Threading.RateLimiting;
using Lumora.Auth.Core.Security;
using Lumora.Auth.Web.Yggdrasil;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// Per-IP request limits: pages and API, game files (higher), account form posts (strict) and the Yggdrasil login
/// policy. Local and private addresses are not limited. Rejections count towards an auto-ban.
/// </summary>
public static class RateLimits
{
    public static IServiceCollection AddLumoraRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var yggdrasilPerMinute = config.GetValue("Lumora:AuthAttemptsPerMinute", 10);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                var http = ctx.HttpContext;
                http.RequestServices.GetRequiredService<AutoBanService>().Record(ClientIp.Of(http), AutoBanService.Offence.RateLimited);
                if (http.Request.Path.StartsWithSegments(YggdrasilEndpoints.Prefix))
                {
                    http.Response.ContentType = "application/json; charset=utf-8";
                    await http.Response.WriteAsJsonAsync(
                        new ErrorResponse("ForbiddenOperationException", "Слишком много попыток входа. Подождите минуту."),
                        YggdrasilJson.Options, ct);
                }
                else
                {
                    http.Response.ContentType = "text/plain; charset=utf-8";
                    await http.Response.WriteAsync("Слишком много запросов. Подождите минуту.", ct);
                }
            };

            o.AddPolicy(YggdrasilEndpoints.AuthRateLimitPolicy, http =>
                Unlimited(http) ? RateLimitPartition.GetNoLimiter("local")
                : RateLimitPartition.GetFixedWindowLimiter("ygg:" + Key(http),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = yggdrasilPerMinute, Window = TimeSpan.FromMinutes(1) }));

            var general = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (Unlimited(http))
                    return RateLimitPartition.GetNoLimiter("local");
                var s = Security(http);
                var files = IsFileRequest(http.Request.Path);
                return RateLimitPartition.GetSlidingWindowLimiter((files ? "files:" : "all:") + Key(http), _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = files ? s.FileRequestsPerMinute : s.RequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                });
            });
            var accountPosts = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (Unlimited(http) || !HttpMethods.IsPost(http.Request.Method) || !http.Request.Path.StartsWithSegments("/Account"))
                    return RateLimitPartition.GetNoLimiter("none");
                return RateLimitPartition.GetSlidingWindowLimiter("acct:" + Key(http), _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = Security(http).AccountPostsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                });
            });
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(general, accountPosts);
        });

        services.AddSingleton<RegistrationGuard>();
        return services;
    }

    private static SecurityOptions Security(HttpContext http) =>
        http.RequestServices.GetRequiredService<IOptions<LumoraOptions>>().Value.Security;

    private static string Key(HttpContext http) => ClientIp.Of(http)?.ToString() ?? "unknown";

    private static bool Unlimited(HttpContext http) =>
        ClientIp.Of(http) is not { } ip || IpNetwork.IsPrivateOrLoopback(ip)
        || http.RequestServices.GetRequiredService<IpGuard>().IsAllowListed(ip);

    /// <summary>Static assets and game files: a page or a modpack needs many of them at once.</summary>
    private static bool IsFileRequest(PathString path) =>
        path.StartsWithSegments("/api/launcher/files") || path.StartsWithSegments("/textures")
        || path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content")
        || path.StartsWithSegments("/lib") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js")
        || path.StartsWithSegments("/fonts") || path.StartsWithSegments("/img");
}

/// <summary>At most N registrations per IP per hour.</summary>
public sealed class RegistrationGuard(IMemoryCache cache, IOptions<LumoraOptions> options)
{
    public bool TryRegister(IPAddress? ip)
    {
        if (ip is null || IpNetwork.IsPrivateOrLoopback(ip))
            return true;
        var key = "reg:" + ip;
        var count = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new int[1];
        })!;
        return Interlocked.Increment(ref count[0]) <= options.Value.Security.RegistrationsPerHour;
    }
}
