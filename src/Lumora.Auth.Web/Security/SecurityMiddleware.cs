using Lumora.Auth.Core.Security;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// First line of defence, before routing: refuses blocked IPs, answers vulnerability-scanner probes with 404
/// (and counts them for auto-ban), hides /healthz from the internet and adds security headers.
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, IpGuard guard, AutoBanService autoBan)
{
    /// <summary>Paths that only vulnerability scanners ask for; Lumora has none of them.</summary>
    private static readonly string[] ScannerPrefixes =
    [
        "/.env", "/.git", "/.svn", "/.aws", "/.ssh", "/.ds_store", "/wp-", "/wordpress", "/phpmyadmin", "/pma", "/myadmin",
        "/xmlrpc", "/cgi-bin", "/vendor/", "/actuator", "/boaform", "/server-status", "/owa/", "/hnap1", "/solr", "/telescope",
        "/druid", "/jenkins", "/manager/html", "/autodiscover", "/ecp/", "/remote/login", "/sdk", "/geoserver", "/.well-known/security.txt.bak",
    ];

    private static readonly string[] ScannerExtensions = [".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".bak", ".sql", ".ini", ".old", ".swp"];

    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = ClientIp.Of(context);
        var (verdict, reason) = guard.Check(ip);
        if (verdict == IpGuard.Verdict.Blocked)
        {
            guard.CountBlocked(reason!);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Доступ запрещён.");
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (IsScannerProbe(path))
        {
            if (verdict != IpGuard.Verdict.AllowListed)
                autoBan.Record(ip, AutoBanService.Offence.ScannerProbe);
            guard.CountBlocked("сканер");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Health checks are for the local machine and the Docker network only.
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) && ip is not null && !IpNetwork.IsPrivateOrLoopback(ip))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            // Replaces Blazor's default "frame-ancestors 'self'" with the full policy.
            h["Content-Security-Policy"] = ContentSecurityPolicy;
            return Task.CompletedTask;
        });

        await next(context);
    }

    public static bool IsScannerProbe(string path)
    {
        var p = path.ToLowerInvariant();
        if (ScannerPrefixes.Any(s => p.StartsWith(s, StringComparison.Ordinal)))
            return true;
        var dot = p.LastIndexOf('.');
        return dot > p.LastIndexOf('/') && ScannerExtensions.Contains(p[dot..]);
    }
}
