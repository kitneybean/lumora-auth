using System.Security.Claims;
using Lumora.Auth.Data;
using Microsoft.AspNetCore.Authorization;

namespace Lumora.Auth.Web.Security;

public static class Policies
{
    /// <summary>Admin role and, unless disabled in settings, a sign-in with two-factor authentication.</summary>
    public const string Admin = "AdminWith2fa";

    /// <summary>A registered Lumora Agent (key in the Authorization header).</summary>
    public const string Agent = "Agent";

    /// <summary>Identity adds amr=mfa to the cookie after a two-factor sign-in.</summary>
    public static bool HasMfa(ClaimsPrincipal user) => user.HasClaim("amr", "mfa");
}

public static class SecuritySetup
{
    public static IServiceCollection AddLumoraSecurity(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<AuditService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditService>());
        services.AddSingleton<IpGuard>();
        services.AddSingleton<AutoBanService>();
        services.AddSingleton<IpBlocklistService>();
        services.AddHostedService(sp => sp.GetRequiredService<IpBlocklistService>());
        services.AddHttpClient(nameof(IpBlocklistService), c =>
        {
            c.Timeout = TimeSpan.FromMinutes(1);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LumoraAuth/1.0 (blocklist update)");
        });
        services.AddLumoraRateLimits(config);

        var requireMfa = config.GetValue("Lumora:Security:RequireAdmin2fa", true);
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p
                .RequireRole(Roles.Admin)
                .RequireAssertion(ctx => !requireMfa || Policies.HasMfa(ctx.User)))
            .AddPolicy(Policies.Agent, p => p
                .AddAuthenticationSchemes(Lumora.Auth.Web.Agents.AgentAuthenticationHandler.SchemeName)
                .RequireRole(Lumora.Auth.Web.Agents.AgentAuthenticationHandler.Role));
        return services;
    }

    public static IApplicationBuilder UseLumoraSecurity(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityMiddleware>();

    /// <summary>Configuration problems an admin should fix, shown on the admin overview.</summary>
    public static IEnumerable<string> ConfigurationWarnings(LumoraOptions o, IHostEnvironment env)
    {
        if (!env.IsDevelopment() && o.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !o.BehindProxy)
            yield return "Сайт работает по HTTPS через прокси, но не знает об этом: включите Lumora__BehindProxy=true " +
                         "и передайте заголовки X-Forwarded-* в nginx. Без этого cookies входа не защищены, а баны видят адрес прокси.";
        if (!o.Security.RequireAdmin2fa)
            yield return "Двухфакторная защита для админов выключена (Lumora:Security:RequireAdmin2fa).";
        if (o.Security.BlocklistUrls.Count == 0)
            yield return "Списки ботнетов отключены (Lumora:Security:BlocklistUrls пуст).";
        if (!string.IsNullOrEmpty(o.Admin.Password))
            yield return "Пароль первого админа всё ещё лежит в настройках (Lumora__Admin__Password) — уберите его из .env.";
    }

    /// <summary>Default or short passwords are not acceptable for an administrator on a public site.</summary>
    public static bool IsWeakAdminPassword(string password) =>
        password.Length < 12 || password is "Admin12345" or "change-me-too" or "change-me";
}
