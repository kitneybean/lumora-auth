using Lumora.Auth.Data;
using Lumora.Auth.Web;
using Lumora.Auth.Web.Components;
using Lumora.Auth.Web.Components.Account;
using Lumora.Auth.Web.Yggdrasil;
using Lumora.Auth.Web.Launcher;
using Lumora.Auth.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Do not advertise the web server in responses.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

var lumoraSection = builder.Configuration.GetSection(LumoraOptions.Section);
builder.Services.Configure<LumoraOptions>(lumoraSection);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("Lumora")
    ?? throw new InvalidOperationException("Connection string 'Lumora' not found.");
// Scoped context for Identity and requests; factory for interactive (Blazor Server) admin pages.
builder.Services.AddDbContext<LumoraDbContext>(options => options.UseNpgsql(connectionString), optionsLifetime: ServiceLifetime.Singleton);
builder.Services.AddDbContextFactory<LumoraDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // E-mail confirmation is off until an SMTP sender is configured.
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<LumoraDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<Lumora.Auth.Web.Settings.SiteSettingsService>();
builder.Services.AddSingleton<Lumora.Auth.Web.Settings.SmtpEmailSender>();
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(sp => sp.GetRequiredService<Lumora.Auth.Web.Settings.SmtpEmailSender>());
builder.Services.AddHealthChecks().AddDbContextCheck<LumoraDbContext>();
builder.Services.AddYggdrasil(builder.Configuration);
builder.Services.AddLumoraSecurity(builder.Configuration);
builder.Services.AddScoped<Lumora.Auth.Web.Security.AdminAudit>();
builder.Services.AddScoped<Lumora.Auth.Web.Profiles.ProfileService>();
builder.Services.AddScoped<Lumora.Auth.Web.Textures.SkinService>();
builder.Services.AddSingleton<Lumora.Auth.Web.Builds.BuildFileStore>();
builder.Services.AddScoped<Lumora.Auth.Web.Builds.BuildService>();
builder.Services.AddScoped<Lumora.Auth.Web.Builds.ServerService>();
builder.Services.AddScoped<Lumora.Auth.Web.Admin.PlayerAdminService>();
builder.Services.AddSingleton<Lumora.Auth.Web.Launcher.AuthlibInjectorProvider>();
builder.Services.AddSingleton<Lumora.Auth.Web.Launcher.GameVersionsService>();
builder.Services.AddHttpClient(nameof(Lumora.Auth.Web.Launcher.AuthlibInjectorProvider), c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient(nameof(Lumora.Auth.Web.Launcher.GameVersionsService), c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient(nameof(Lumora.Auth.Web.Builds.BuildService), c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("LumoraAuth/1.0 (modpack import)");
});

// Keep cookie keys across restarts and container rebuilds.
var dataRoot = Path.GetFullPath(lumoraSection["DataPath"] ?? "data", builder.Environment.ContentRootPath);
builder.Services.AddDataProtection()
    .SetApplicationName("Lumora.Auth")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys", "data-protection")));

// Behind a reverse proxy (Caddy/nginx): trust X-Forwarded-* so the site knows the real scheme and client IP.
if (lumoraSection.GetValue<bool>("BehindProxy"))
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        // The proxy runs in the same private Docker network; the app port is not published.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (lumoraSection.GetValue<bool>("BehindProxy"))
    app.UseForwardedHeaders();

// Blocked IPs, scanner probes, hidden /healthz and security headers — before anything else runs.
app.UseLumoraSecurity();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!lumoraSection.GetValue<bool>(nameof(LumoraOptions.SkipDatabaseInit)))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
    await app.Services.GetRequiredService<Lumora.Auth.Web.Settings.SiteSettingsService>().LoadAsync();
}

// Create or load the texture signing key now rather than on the first game login.
_ = app.Services.GetRequiredService<Lumora.Auth.Core.Yggdrasil.SigningKey>();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseYggdrasilApiLocation();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapYggdrasil();
app.MapTextures();
app.MapLauncherApi();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
