using Lumora.Auth.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web;

/// <summary>Applies migrations and seeds roles and the first administrator.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        var db = sp.GetRequiredService<LumoraDbContext>();
        await db.Database.MigrateAsync(ct);

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        var admin = sp.GetRequiredService<IOptions<LumoraOptions>>().Value.Admin;
        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password))
            return;

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(admin.Email);
        if (user is null)
        {
            // A public site must not get an administrator with a default or short password.
            var env = sp.GetRequiredService<IHostEnvironment>();
            if (!env.IsDevelopment() && Security.SecuritySetup.IsWeakAdminPassword(admin.Password))
            {
                logger.LogError("Administrator {Email} was NOT created: the password is shorter than 12 characters or a default one. " +
                                "Set a strong Lumora__Admin__Password and restart.", admin.Email);
                return;
            }
            user = new ApplicationUser { UserName = admin.Email, Email = admin.Email, EmailConfirmed = true };
            var result = await userManager.CreateAsync(user, admin.Password);
            if (!result.Succeeded)
            {
                logger.LogError("Failed to create administrator: {Errors}",
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Created administrator {Email}", admin.Email);
        }

        if (!await userManager.IsInRoleAsync(user, Roles.Admin))
            await userManager.AddToRoleAsync(user, Roles.Admin);
    }
}
