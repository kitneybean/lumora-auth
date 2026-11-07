using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Profiles;
using Lumora.Auth.Core.Yggdrasil;
using Lumora.Auth.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Tests;

/// <summary>End-to-end checks of the Yggdrasil API as authlib-injector uses it.</summary>
[Collection(AppCollection.Name)]
public class YggdrasilApiTests(LumoraAppFactory app)
{
    private const string Api = "/api/yggdrasil";
    private readonly HttpClient _http = app.CreateClient();

    [Fact]
    public async Task Metadata_describes_the_server_and_publishes_the_signing_key()
    {
        var response = await _http.GetAsync(Api + "/");
        response.EnsureSuccessStatusCode();
        var json = await Json(response);

        Assert.Equal("Lumora", json.GetProperty("meta").GetProperty("serverName").GetString());
        Assert.True(json.GetProperty("meta").GetProperty("feature.non_email_login").GetBoolean());
        Assert.Equal("auth.test", json.GetProperty("skinDomains")[0].GetString());
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", json.GetProperty("signaturePublickey").GetString());

        // Without the trailing slash too.
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Api)).StatusCode);
    }

    [Fact]
    public async Task Site_pages_advertise_the_api_location()
    {
        var response = await _http.GetAsync("/healthz");
        Assert.Equal("/api/yggdrasil/", response.Headers.GetValues("X-Authlib-Injector-API-Location").Single());
    }

    [Fact]
    public async Task Authenticate_by_email_or_name_returns_token_and_profile()
    {
        var user = await app.CreatePlayerAsync("Alex_01");

        var byEmail = await Authenticate("alex_01@example.com", "Password123", clientToken: "client-1", requestUser: true);
        Assert.Equal("client-1", byEmail.GetProperty("clientToken").GetString());
        Assert.Equal("Alex_01", byEmail.GetProperty("selectedProfile").GetProperty("name").GetString());
        Assert.Single(byEmail.GetProperty("availableProfiles").EnumerateArray());
        Assert.Equal(user.Id.Replace("-", ""), byEmail.GetProperty("user").GetProperty("id").GetString());

        // In-game name, any letter case; a client token is generated when missing.
        var byName = await Authenticate("ALEX_01", "Password123");
        Assert.False(string.IsNullOrEmpty(byName.GetProperty("clientToken").GetString()));
        Assert.False(byName.TryGetProperty("user", out _));
    }

    [Fact]
    public async Task Authenticate_with_wrong_password_is_forbidden()
    {
        await app.CreatePlayerAsync("WrongPw");

        var response = await _http.PostAsJsonAsync(Api + "/authserver/authenticate",
            new { username = "WrongPw", password = "nope", agent = new { name = "Minecraft", version = 1 } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("ForbiddenOperationException", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Join_then_hasJoined_returns_signed_profile_with_skin()
    {
        var user = await app.CreatePlayerAsync("Joiner");
        const string skinHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await app.WithDbAsync(async db =>
        {
            var profile = await db.Profiles.SingleAsync(p => p.UserId == user.Id);
            profile.SkinHash = skinHash;
            profile.SkinModel = SkinModel.Slim;
        });

        var auth = await Authenticate("Joiner", "Password123");
        var accessToken = auth.GetProperty("accessToken").GetString();
        var profileId = auth.GetProperty("selectedProfile").GetProperty("id").GetString();

        var join = await _http.PostAsJsonAsync(Api + "/sessionserver/session/minecraft/join",
            new { accessToken, selectedProfile = profileId, serverId = "server-hash-1" });
        Assert.Equal(HttpStatusCode.NoContent, join.StatusCode);

        var joined = await _http.GetAsync(Api + "/sessionserver/session/minecraft/hasJoined?username=Joiner&serverId=server-hash-1");
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var profileJson = await Json(joined);
        Assert.Equal(profileId, profileJson.GetProperty("id").GetString());

        var textures = profileJson.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "textures");
        var value = textures.GetProperty("value").GetString()!;
        var signature = textures.GetProperty("signature").GetString()!;
        Assert.True(SigningKey.Verify(await PublicKey(), value, signature));

        using var decoded = TexturesProperty.Decode(value);
        var skin = decoded.RootElement.GetProperty("textures").GetProperty("SKIN");
        Assert.Equal($"{LumoraAppFactory.PublicUrl}/textures/{skinHash}", skin.GetProperty("url").GetString());
        Assert.Equal("slim", skin.GetProperty("metadata").GetProperty("model").GetString());
        Assert.Equal("Joiner", decoded.RootElement.GetProperty("profileName").GetString());

        // Someone else cannot use that server id.
        var other = await _http.GetAsync(Api + "/sessionserver/session/minecraft/hasJoined?username=Someone&serverId=server-hash-1");
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task Join_with_someone_elses_profile_is_rejected()
    {
        await app.CreatePlayerAsync("Honest");
        var victim = await app.CreatePlayerAsync("Victim");
        var auth = await Authenticate("Honest", "Password123");
        var victimProfile = await FindProfileId(victim.Id);

        var join = await _http.PostAsJsonAsync(Api + "/sessionserver/session/minecraft/join",
            new { accessToken = auth.GetProperty("accessToken").GetString(), selectedProfile = victimProfile, serverId = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, join.StatusCode);
    }

    [Fact]
    public async Task Refresh_replaces_the_token_and_invalidate_revokes_it()
    {
        await app.CreatePlayerAsync("Refresher");
        var auth = await Authenticate("Refresher", "Password123", clientToken: "c");
        var first = auth.GetProperty("accessToken").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/authserver/validate", new { accessToken = first, clientToken = "c" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = first, clientToken = "other" })).StatusCode);

        var refresh = await Post("/authserver/refresh", new { accessToken = first, clientToken = "c", requestUser = true });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var refreshed = await Json(refresh);
        var second = refreshed.GetProperty("accessToken").GetString();
        Assert.NotEqual(first, second);
        Assert.Equal("Refresher", refreshed.GetProperty("selectedProfile").GetProperty("name").GetString());
        Assert.True(refreshed.TryGetProperty("user", out _));

        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = first })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/authserver/validate", new { accessToken = second })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/authserver/invalidate", new { accessToken = second })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = second })).StatusCode);
    }

    [Fact]
    public async Task Signout_revokes_every_token_of_the_user()
    {
        await app.CreatePlayerAsync("Leaver");
        var a = (await Authenticate("Leaver", "Password123")).GetProperty("accessToken").GetString();
        var b = (await Authenticate("Leaver", "Password123")).GetProperty("accessToken").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/signout", new { username = "Leaver", password = "bad" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/authserver/signout", new { username = "Leaver", password = "Password123" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = a })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = b })).StatusCode);
    }

    [Fact]
    public async Task Banned_player_cannot_log_in_or_use_old_tokens()
    {
        var user = await app.CreatePlayerAsync("Griefer");
        var token = (await Authenticate("Griefer", "Password123")).GetProperty("accessToken").GetString();

        await app.WithDbAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Id == user.Id);
            u.BanReason = "гриферство";
            u.LockoutEnabled = true;
            u.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
        });

        var login = await Post("/authserver/authenticate", new { username = "Griefer", password = "Password123" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("гриферство", (await Json(login)).GetProperty("errorMessage").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/authserver/validate", new { accessToken = token })).StatusCode);
    }

    [Fact]
    public async Task Profile_query_is_unsigned_unless_asked()
    {
        var user = await app.CreatePlayerAsync("Looked_Up");
        var id = await FindProfileId(user.Id);

        var unsigned = await Json(await _http.GetAsync($"{Api}/sessionserver/session/minecraft/profile/{id}"));
        Assert.All(unsigned.GetProperty("properties").EnumerateArray(), p => Assert.False(p.TryGetProperty("signature", out _)));

        var signed = await Json(await _http.GetAsync($"{Api}/sessionserver/session/minecraft/profile/{id}?unsigned=false"));
        Assert.All(signed.GetProperty("properties").EnumerateArray(), p => Assert.True(p.TryGetProperty("signature", out _)));

        var missing = await _http.GetAsync($"{Api}/sessionserver/session/minecraft/profile/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
    }

    [Fact]
    public async Task Names_resolve_case_insensitively()
    {
        await app.CreatePlayerAsync("NameOne");
        await app.CreatePlayerAsync("NameTwo");

        var batch = await Post("/api/profiles/minecraft", new[] { "nameone", "NAMETWO", "nobody_here" });
        var names = (await Json(batch)).EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal(["NameOne", "NameTwo"], names);

        var single = await _http.GetAsync(Api + "/api/users/profiles/minecraft/nameone");
        Assert.Equal("NameOne", (await Json(single)).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await _http.GetAsync(Api + "/api/users/profiles/minecraft/nobody_here")).StatusCode);
    }

    [Fact]
    public async Task Textures_are_served_by_hash()
    {
        var hash = new string('a', 64);
        var dir = Path.Combine(app.DataPath, "textures");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, hash + ".png"), [0x89, 0x50, 0x4E, 0x47]);

        var response = await _http.GetAsync("/textures/" + hash);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/textures/" + new string('b', 64))).StatusCode);
    }

    [Fact]
    public async Task Skin_upload_with_token_updates_profile_textures()
    {
        await app.CreatePlayerAsync("Uploader");
        var auth = await Authenticate("Uploader", "Password123");
        var token = auth.GetProperty("accessToken").GetString();
        var id = auth.GetProperty("selectedProfile").GetProperty("id").GetString();
        var url = $"{Api}/api/user/profile/{id}/skin";

        // No token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PutAsync(url, SkinForm(TestImages.Png(64, 64), "slim"))).StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = SkinForm(TestImages.Png(64, 64), "slim") };
        put.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(put)).StatusCode);

        var skin = await SkinOf(id!);
        Assert.Equal("slim", skin!.Value.GetProperty("metadata").GetProperty("model").GetString());
        var texturePath = new Uri(skin.Value.GetProperty("url").GetString()!).AbsolutePath;
        var png = await _http.GetAsync(texturePath);
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);

        // Wrong size is rejected with a readable message.
        var bad = new HttpRequestMessage(HttpMethod.Put, url) { Content = SkinForm(TestImages.Png(32, 32), "") };
        bad.Headers.Authorization = new("Bearer", token);
        var badResponse = await _http.SendAsync(bad);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
        Assert.Contains("64×64", (await Json(badResponse)).GetProperty("errorMessage").GetString());

        var delete = new HttpRequestMessage(HttpMethod.Delete, url);
        delete.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(delete)).StatusCode);
        Assert.Null(await SkinOf(id!));
    }

    [Fact]
    public async Task Skin_of_another_profile_cannot_be_changed()
    {
        await app.CreatePlayerAsync("Owner_A");
        var other = await app.CreatePlayerAsync("Owner_B");
        var token = (await Authenticate("Owner_A", "Password123")).GetProperty("accessToken").GetString();

        var put = new HttpRequestMessage(HttpMethod.Put, $"{Api}/api/user/profile/{await FindProfileId(other.Id)}/skin")
        {
            Content = SkinForm(TestImages.Png(64, 64), ""),
        };
        put.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(put)).StatusCode);
    }

    private static MultipartFormDataContent SkinForm(byte[] png, string model)
    {
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new("image/png");
        return new MultipartFormDataContent { { new StringContent(model), "model" }, { file, "file", "skin.png" } };
    }

    private async Task<JsonElement?> SkinOf(string profileId)
    {
        var profile = await Json(await _http.GetAsync($"{Api}/sessionserver/session/minecraft/profile/{profileId}"));
        var value = profile.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "textures")
            .GetProperty("value").GetString()!;
        using var decoded = TexturesProperty.Decode(value);
        return decoded.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin) ? skin.Clone() : null;
    }

    // ------------------------------------------------------------------

    private Task<HttpResponseMessage> Post(string path, object body) => _http.PostAsJsonAsync(Api + path, body);

    private async Task<JsonElement> Authenticate(string username, string password, string? clientToken = null, bool requestUser = false)
    {
        var response = await Post("/authserver/authenticate", new
        {
            username,
            password,
            clientToken,
            requestUser,
            agent = new { name = "Minecraft", version = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private async Task<string> PublicKey() =>
        (await Json(await _http.GetAsync(Api + "/"))).GetProperty("signaturePublickey").GetString()!;

    private async Task<string> FindProfileId(string userId)
    {
        string? id = null;
        await app.WithDbAsync(async db => id = MinecraftUuid.ToUnsigned((await db.Profiles.SingleAsync(p => p.UserId == userId)).Id));
        return id!;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
