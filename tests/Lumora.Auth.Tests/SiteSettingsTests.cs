using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lumora.Auth.Data;
using Lumora.Auth.Tests.Infrastructure;
using Lumora.Auth.Web.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Tests;

[Collection(AppCollection.Name)]
public class SiteSettingsTests(LumoraAppFactory app)
{
    [Fact]
    public async Task Settings_apply_immediately_and_keep_smtp_password_encrypted()
    {
        var site = app.Services.GetRequiredService<SiteSettingsService>();
        var original = site.Current;
        try
        {
            var edited = site.Current;
            edited.ServerName = "Test Realm";
            edited.TokenLifetimeDays = 3;
            edited.SmtpEnabled = true;
            edited.SmtpHost = "smtp.example.com";
            edited.MailFromAddress = "noreply@example.com";
            await site.SaveAsync(edited, "s3cret-app-password");

            // Metadata shows the new name.
            var http = app.CreateClient();
            var meta = JsonDocument.Parse(await http.GetStringAsync("/api/yggdrasil/")).RootElement;
            Assert.Equal("Test Realm", meta.GetProperty("meta").GetProperty("serverName").GetString());

            // New tokens use the new lifetime.
            await app.CreatePlayerAsync("Lifetime");
            var auth = await http.PostAsJsonAsync("/api/yggdrasil/authserver/authenticate", new { username = "Lifetime", password = "Password123" });
            var token = JsonDocument.Parse(await auth.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
            await app.WithDbAsync(async db =>
            {
                var row = await db.Tokens.SingleAsync(t => t.AccessToken == token);
                Assert.InRange(row.ExpiresAt - row.IssuedAt, TimeSpan.FromDays(2.9), TimeSpan.FromDays(3.1));
                var stored = await db.SiteSettings.SingleAsync();
                Assert.DoesNotContain("s3cret", stored.SmtpPasswordProtected);
            });
            Assert.Equal("s3cret-app-password", site.SmtpPassword(site.Current));

            // With working mail, e-mail confirmation can be enforced by Identity.
            edited.RequireConfirmedEmail = true;
            await site.SaveAsync(edited, null);
            Assert.True(app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.SignIn.RequireConfirmedAccount);
        }
        finally
        {
            await site.SaveAsync(original, "");
        }
        Assert.False(app.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.SignIn.RequireConfirmedAccount);
    }

    [Fact]
    public async Task Unconfirmed_email_gets_a_clear_message_when_confirmation_is_required()
    {
        var site = app.Services.GetRequiredService<SiteSettingsService>();
        var original = site.Current;
        await app.CreatePlayerAsync("Unconfirmed");
        try
        {
            var edited = site.Current;
            edited.SmtpEnabled = true;
            edited.SmtpHost = "smtp.example.com";
            edited.MailFromAddress = "noreply@example.com";
            edited.RequireConfirmedEmail = true;
            await site.SaveAsync(edited, null);

            var response = await app.CreateClient().PostAsJsonAsync("/api/yggdrasil/authserver/authenticate",
                new { username = "Unconfirmed", password = "Password123" });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errorMessage").GetString();
            Assert.Contains("Подтвердите e-mail", error);
        }
        finally
        {
            await site.SaveAsync(original, null);
        }
    }
}
