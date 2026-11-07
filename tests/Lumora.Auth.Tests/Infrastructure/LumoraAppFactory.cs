using Lumora.Auth.Data;
using Lumora.Auth.Web.Profiles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Lumora.Auth.Tests.Infrastructure;

/// <summary>
/// Runs the real site against a throw-away PostgreSQL container (needs Docker).
/// </summary>
public sealed class LumoraAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PublicUrl = "https://auth.test";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "lumora-tests-" + Guid.NewGuid().ToString("N"));

    public string DataPath => _dataPath;

    public Task InitializeAsync() => _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        await _db.DisposeAsync();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Lumora", _db.GetConnectionString());
        builder.UseSetting("Lumora:DataPath", _dataPath);
        builder.UseSetting("Lumora:PublicUrl", PublicUrl);
        builder.UseSetting("Lumora:AuthAttemptsPerMinute", "10000");
    }

    /// <summary>Creates an account with an in-game profile.</summary>
    public async Task<ApplicationUser> CreatePlayerAsync(string name, string password = "Password123")
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{name.ToLowerInvariant()}@example.com";
        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

        var error = await scope.ServiceProvider.GetRequiredService<ProfileService>().CreateAsync(user.Id, name);
        Assert.Null(error);
        return user;
    }

    public async Task WithDbAsync(Func<LumoraDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumoraDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }
}
