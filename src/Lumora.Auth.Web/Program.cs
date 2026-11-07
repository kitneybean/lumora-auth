using Lumora.Auth.Data;
using Lumora.Auth.Web;
using Lumora.Auth.Web.Components;
using Lumora.Auth.Web.Components.Account;
using Lumora.Auth.Web.Yggdrasil;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddDbContext<LumoraDbContext>(options => options.UseNpgsql(connectionString));
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

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddHealthChecks().AddDbContextCheck<LumoraDbContext>();
builder.Services.AddYggdrasil(builder.Configuration);
builder.Services.AddScoped<Lumora.Auth.Web.Profiles.ProfileService>();
builder.Services.AddScoped<Lumora.Auth.Web.Textures.SkinService>();

// Keep cookie keys across restarts and container rebuilds.
var dataRoot = Path.GetFullPath(lumoraSection["DataPath"] ?? "data", builder.Environment.ContentRootPath);
builder.Services.AddDataProtection()
    .SetApplicationName("Lumora.Auth")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataRoot, "keys", "data-protection")));

var app = builder.Build();

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
    await DatabaseInitializer.InitializeAsync(app.Services);

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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
