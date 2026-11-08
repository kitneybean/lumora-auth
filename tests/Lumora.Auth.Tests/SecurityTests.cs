using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Security;
using Lumora.Auth.Data;
using Lumora.Auth.Tests.Infrastructure;
using Lumora.Auth.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lumora.Auth.Tests;

public class IpNetworkTests
{
    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.200", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("198.51.100.7", "198.51.100.7", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("203.0.113.0/24", "::ffff:203.0.113.9", true)]
    public void Contains(string network, string ip, bool expected) =>
        Assert.Equal(expected, IpNetwork.Parse(network).Contains(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.18.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void Private_addresses_are_recognised(string ip) => Assert.True(IpNetwork.IsPrivateOrLoopback(IPAddress.Parse(ip)));

    [Fact]
    public void Range_set_merges_and_searches()
    {
        var set = new IpRangeSet(IpRangeSet.ParseList("""
            # FireHOL-style list
            203.0.113.0/25
            203.0.113.128/25 ; adjacent, merged
            198.51.100.7
            bad line
            """));
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(IPAddress.Parse("203.0.113.250")));
        Assert.True(set.Contains(IPAddress.Parse("198.51.100.7")));
        Assert.False(set.Contains(IPAddress.Parse("198.51.100.8")));
    }

    [Theory]
    [InlineData("/.env", true)]
    [InlineData("/wp-login.php", true)]
    [InlineData("/index.php", true)]
    [InlineData("/phpmyadmin/", true)]
    [InlineData("/admin", false)]
    [InlineData("/Account/Login", false)]
    [InlineData("/textures/abc", false)]
    [InlineData("/lib/skinview3d/skinview3d.bundle.js", false)]
    public void Scanner_paths(string path, bool expected) => Assert.Equal(expected, SecurityMiddleware.IsScannerProbe(path));

    [Theory]
    [InlineData("Admin12345", true)]
    [InlineData("short", true)]
    [InlineData("a-long-unique-passphrase", false)]
    public void Weak_admin_passwords(string password, bool weak) => Assert.Equal(weak, SecuritySetup.IsWeakAdminPassword(password));
}

[Collection(AppCollection.Name)]
public class SecurityTests(LumoraAppFactory app)
{
    private IpGuard Guard => app.Services.GetRequiredService<IpGuard>();

    [Fact]
    public async Task Pages_have_security_headers()
    {
        var response = await app.ClientFrom("198.51.100.10").GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }

    [Fact]
    public async Task Healthz_is_hidden_from_the_internet()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await app.ClientFrom("198.51.100.11").GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.ClientFrom("172.18.0.2").GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Scanner_gets_404_and_then_an_automatic_ban()
    {
        var client = app.ClientFrom("198.51.100.20");
        foreach (var path in new[] { "/.env", "/wp-login.php", "/.git/config" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);

        await WaitUntilBlockedAsync("198.51.100.20");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
        await app.WithDbAsync(async db =>
            Assert.True(await db.IpRules.AnyAsync(r => r.Network == "198.51.100.20" && r.Source == "auto" && r.ExpiresAt != null)));
    }

    [Fact]
    public async Task Password_guessing_from_the_launcher_is_banned_and_journaled()
    {
        await app.CreatePlayerAsync("Victim_PW");
        var client = app.ClientFrom("198.51.100.30");
        for (var i = 0; i < 10; i++)
            await client.PostAsJsonAsync("/api/yggdrasil/authserver/authenticate", new { username = "Victim_PW", password = "guess" + i });

        await WaitUntilBlockedAsync("198.51.100.30");
        await app.Services.GetRequiredService<AuditService>().FlushAsync();
        await app.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEvents.CountAsync(e => e.Action == "login.launcher" && !e.Success && e.Ip == "198.51.100.30") >= 10);
            Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "ip.autoban" && e.Ip == "198.51.100.30"));
        });
    }

    [Fact]
    public async Task Blocklisted_networks_are_refused_unless_allow_listed()
    {
        Guard.SetBlocklist(new IpRangeSet([IpNetwork.Parse("192.0.2.0/24")]), DateTimeOffset.UtcNow);
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await app.ClientFrom("192.0.2.15").GetAsync("/")).StatusCode);

            Assert.Null(await Guard.AddManualAsync("192.0.2.15", IpRuleKind.Allow, "admin home", null, "test"));
            Assert.Equal(HttpStatusCode.OK, (await app.ClientFrom("192.0.2.15").GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.ClientFrom("192.0.2.16").GetAsync("/")).StatusCode);
        }
        finally
        {
            Guard.SetBlocklist(IpRangeSet.Empty, DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public async Task Manual_block_and_unblock()
    {
        Assert.Null(await Guard.AddManualAsync("203.0.113.40", IpRuleKind.Block, "test", TimeSpan.FromDays(1), "test"));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.ClientFrom("203.0.113.40").GetAsync("/")).StatusCode);

        var rule = (await Guard.ListAsync()).Single(r => r.Network == "203.0.113.40");
        await Guard.RemoveAsync(rule.Id, "test");
        Assert.Equal(HttpStatusCode.OK, (await app.ClientFrom("203.0.113.40").GetAsync("/")).StatusCode);

        Assert.NotNull(await Guard.AddManualAsync("not-an-ip", IpRuleKind.Block, null, null, "test"));
    }

    [Fact]
    public async Task Admin_panel_requires_a_two_factor_sign_in()
    {
        const string password = "Admin-Password-123";
        var user = await app.CreatePlayerAsync("Admin_2fa", password);
        string key;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var u = (await users.FindByIdAsync(user.Id))!;
            await users.AddToRoleAsync(u, Roles.Admin);

            // Without 2FA: signed in, but the admin panel asks to enable it.
            var plain = app.ClientFrom("203.0.113.50");
            await SignInAsync(plain, "admin_2fa@example.com", password);
            var page = await plain.GetStringAsync("/admin");
            Assert.Contains("Нужна двухфакторная защита", page);

            await users.ResetAuthenticatorKeyAsync(u);
            await users.SetTwoFactorEnabledAsync(u, true);
            key = (await users.GetAuthenticatorKeyAsync(u))!;
        }

        // With a code from the authenticator app the admin panel opens.
        var client = app.ClientFrom("203.0.113.51");
        var afterPassword = await SignInAsync(client, "admin_2fa@example.com", password);
        Assert.Contains("LoginWith2fa", afterPassword);
        var form = await client.GetStringAsync(afterPassword);
        var posted = await client.PostAsync(afterPassword, FormFrom(form, new()
        {
            ["Input.TwoFactorCode"] = Totp(key),
            ["Input.RememberMachine"] = "true",
        }));
        Assert.True(posted.IsSuccessStatusCode);
        var admin = await client.GetStringAsync("/admin");
        Assert.DoesNotContain("Нужна двухфакторная защита", admin);
        Assert.Contains("Админка", admin);

        // Admins are always asked for the code, even after "remember this computer".
        var home = await client.GetStringAsync("/");
        var logout = await client.PostAsync("/Account/Logout", FormFrom(home, new() { ["ReturnUrl"] = "" }));
        Assert.True(logout.IsSuccessStatusCode, $"{(int)logout.StatusCode} {await logout.Content.ReadAsStringAsync()}");
        Assert.DoesNotContain("Админка", await client.GetStringAsync("/admin"));
        var again = await SignInAsync(client, "admin_2fa@example.com", password);
        Assert.Contains("LoginWith2fa", again);
    }

    // ------------------------------------------------------------------

    private async Task WaitUntilBlockedAsync(string ip)
    {
        for (var i = 0; i < 40 && Guard.Check(IPAddress.Parse(ip)).Verdict != IpGuard.Verdict.Blocked; i++)
            await Task.Delay(100);
        Assert.Equal(IpGuard.Verdict.Blocked, Guard.Check(IPAddress.Parse(ip)).Verdict);
    }

    /// <summary>Posts the login form; returns the URL it redirected to.</summary>
    private static async Task<string> SignInAsync(HttpClient client, string login, string password)
    {
        var page = await client.GetStringAsync("/Account/Login");
        var response = await client.PostAsync("/Account/Login", FormFrom(page, new()
        {
            ["Input.Email"] = login,
            ["Input.Password"] = password,
        }));
        return response.RequestMessage!.RequestUri!.PathAndQuery;
    }

    /// <summary>Hidden fields of the first form on the page (antiforgery, handler) plus the given values.</summary>
    private static FormUrlEncodedContent FormFrom(string html, Dictionary<string, string> values)
    {
        var fields = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var name = Regex.Match(m.Value, "name=\"([^\"]+)\"").Groups[1].Value;
            var value = WebUtility.HtmlDecode(Regex.Match(m.Value, "value=\"([^\"]*)\"").Groups[1].Value);
            if (name.Length > 0 && !fields.ContainsKey(name))
                fields[name] = value;
        }
        foreach (var (k, v) in values)
            fields[k] = v;
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>RFC 6238 code (30 s, 6 digits) like an authenticator app.</summary>
    private static string Totp(string base32Key)
    {
        var key = FromBase32(base32Key);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var data = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(data);
        var hash = HMACSHA1.HashData(key, data);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3]) % 1_000_000;
        return code.ToString("D6");
    }

    private static byte[] FromBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
