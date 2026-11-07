using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Profiles;
using Lumora.Auth.Core.Yggdrasil;
using Lumora.Auth.Core.Textures;
using Lumora.Auth.Web.Textures;
using Lumora.Auth.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Yggdrasil;

/// <summary>
/// Yggdrasil API for authlib-injector: <c>/api/yggdrasil/...</c>.
/// Minecraft (client and server) talks to these endpoints instead of Mojang's.
/// </summary>
public static class YggdrasilEndpoints
{
    public const string Prefix = "/api/yggdrasil";
    public const string AuthRateLimitPolicy = "yggdrasil-auth";
    public const int MaxBatchNames = 10;

    public static IEndpointRouteBuilder MapYggdrasil(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(Prefix)
            .AddEndpointFilter(HandleErrors)
            .DisableAntiforgery()
            .WithTags("Yggdrasil");

        api.MapGet("/", Metadata);

        var auth = api.MapGroup("/authserver");
        auth.MapPost("/authenticate", Authenticate).RequireRateLimiting(AuthRateLimitPolicy);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/validate", Validate);
        auth.MapPost("/invalidate", Invalidate);
        auth.MapPost("/signout", Signout).RequireRateLimiting(AuthRateLimitPolicy);

        var session = api.MapGroup("/sessionserver/session/minecraft");
        session.MapPost("/join", Join);
        session.MapGet("/hasJoined", HasJoined);
        session.MapGet("/profile/{uuid}", GetProfile);

        api.MapPost("/api/profiles/minecraft", LookupNames);
        api.MapGet("/api/users/profiles/minecraft/{name}", LookupName);

        // Texture upload (Bearer access token), used by launchers.
        api.MapPut("/api/user/profile/{uuid}/{textureType}", UploadTexture);
        api.MapDelete("/api/user/profile/{uuid}/{textureType}", DeleteTexture);

        // Minimal Minecraft Services API used by 1.19+ clients.
        api.MapGet("/minecraftservices/player/attributes", PlayerAttributes);
        api.MapGet("/minecraftservices/privileges/blocklist", () => Json(new { blockedProfiles = Array.Empty<string>() }));

        return app;
    }

    // ---------------------------------------------------------------- metadata

    private static IResult Metadata(IOptions<LumoraOptions> options, Settings.SiteSettingsService site, SigningKey key)
    {
        var o = options.Value;
        var publicUrl = o.PublicUrl.TrimEnd('/');
        var meta = new Dictionary<string, object?>
        {
            ["serverName"] = site.Current.ServerName,
            ["implementationName"] = "Lumora Auth",
            ["implementationVersion"] = typeof(YggdrasilEndpoints).Assembly.GetName().Version?.ToString(3),
            ["links"] = new { homepage = publicUrl + "/", register = publicUrl + "/Account/Register" },
            // Players may log in with their in-game name as well as e-mail.
            ["feature.non_email_login"] = true,
        };
        return Json(new
        {
            meta,
            skinDomains = new[] { new Uri(publicUrl).Host },
            signaturePublickey = key.PublicKeyPem,
        });
    }

    // ---------------------------------------------------------------- authserver

    private static async Task<IResult> Authenticate(
        AuthenticateRequest request, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        LumoraDbContext db, TokenService tokens, CancellationToken ct)
    {
        var user = await CheckCredentialsAsync(request.Username, request.Password, users, signIn, db);
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var clientToken = string.IsNullOrWhiteSpace(request.ClientToken) ? Guid.NewGuid().ToString("N") : request.ClientToken;
        var token = await tokens.IssueAsync(user.Id, clientToken, profile?.Id, ct);

        var profileRef = profile is null ? null : ProfileWriter.Ref(profile);
        return Json(new AuthenticateResponse(
            token.AccessToken,
            token.ClientToken,
            profileRef is null ? [] : [profileRef],
            profileRef,
            request.RequestUser ? UserOf(user) : null));
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request, LumoraDbContext db, TokenService tokens, CancellationToken ct)
    {
        var old = await tokens.FindRefreshableAsync(request.AccessToken, request.ClientToken, ct)
            ?? throw YggdrasilException.InvalidToken();

        var profileId = old.ProfileId;
        if (request.SelectedProfile is { } selected)
        {
            if (old.ProfileId is not null)
                throw YggdrasilException.IllegalArgument("Access token already has a profile assigned.");
            if (!MinecraftUuid.TryParse(selected.Id, out var id)
                || !await db.Profiles.AnyAsync(p => p.Id == id && p.UserId == old.UserId, ct))
                throw YggdrasilException.Forbidden("Invalid profile.");
            profileId = id;
        }

        await tokens.RevokeAsync(old.AccessToken, ct);
        var token = await tokens.IssueAsync(old.UserId, old.ClientToken, profileId, ct);
        var profile = profileId is null ? null : await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct);

        UserDto? user = null;
        if (request.RequestUser && await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token.UserId, ct) is { } u)
            user = UserOf(u);

        return Json(new RefreshResponse(token.AccessToken, token.ClientToken,
            profile is null ? null : ProfileWriter.Ref(profile), user));
    }

    private static async Task<IResult> Validate(TokenRequest request, TokenService tokens, CancellationToken ct) =>
        await tokens.FindUsableAsync(request.AccessToken, request.ClientToken, ct) is null
            ? throw YggdrasilException.InvalidToken()
            : Results.NoContent();

    private static async Task<IResult> Invalidate(TokenRequest request, TokenService tokens, CancellationToken ct)
    {
        await tokens.RevokeAsync(request.AccessToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Signout(
        SignoutRequest request, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
        LumoraDbContext db, TokenService tokens, CancellationToken ct)
    {
        var user = await CheckCredentialsAsync(request.Username, request.Password, users, signIn, db);
        await tokens.RevokeAllAsync(user.Id, ct);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- sessionserver

    private static async Task<IResult> Join(
        JoinRequest request, HttpContext http, TokenService tokens, JoinStore joins, CancellationToken ct)
    {
        var token = await tokens.FindUsableAsync(request.AccessToken, null, ct);
        if (token?.ProfileId is not { } profileId
            || !MinecraftUuid.TryParse(request.SelectedProfile, out var selected)
            || selected != profileId)
            throw YggdrasilException.InvalidToken();
        if (string.IsNullOrEmpty(request.ServerId))
            throw YggdrasilException.IllegalArgument("serverId is required.");

        joins.Add(request.ServerId, profileId, http.Connection.RemoteIpAddress?.ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> HasJoined(
        [FromQuery] string? username, [FromQuery] string? serverId, [FromQuery] string? ip,
        JoinStore joins, LumoraDbContext db, ProfileWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(serverId) || joins.Find(serverId) is not { } entry)
            return Results.NoContent();
        if (!string.IsNullOrEmpty(ip) && entry.Ip is not null && entry.Ip != ip)
            return Results.NoContent();

        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == entry.ProfileId, ct);
        if (profile is null || !string.Equals(profile.Name, username, StringComparison.OrdinalIgnoreCase))
            return Results.NoContent();

        return Json(writer.Write(profile, signed: true));
    }

    private static async Task<IResult> GetProfile(
        string uuid, [FromQuery] bool? unsigned, LumoraDbContext db, ProfileWriter writer, CancellationToken ct)
    {
        if (!MinecraftUuid.TryParse(uuid, out var id))
            return Results.NoContent();
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return profile is null
            ? Results.NoContent()
            : Json(writer.Write(profile, signed: unsigned == false));
    }

    // ---------------------------------------------------------------- name lookup

    private static async Task<IResult> LookupNames(string[] names, LumoraDbContext db, CancellationToken ct)
    {
        var wanted = names.Where(PlayerName.IsValid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count > MaxBatchNames)
            throw YggdrasilException.IllegalArgument($"Not more than {MaxBatchNames} profile names per request.");

        var found = await db.Profiles.AsNoTracking().Where(p => wanted.Contains(p.Name)).ToListAsync(ct);
        return Json(found.Select(ProfileWriter.Ref).ToList());
    }

    private static async Task<IResult> LookupName(string name, LumoraDbContext db, CancellationToken ct)
    {
        if (!PlayerName.IsValid(name))
            return Results.NoContent();
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name, ct);
        return profile is null ? Results.NoContent() : Json(ProfileWriter.Ref(profile));
    }

    private static IResult PlayerAttributes() => Json(new
    {
        privileges = new
        {
            onlineChat = new { enabled = true },
            multiplayerServer = new { enabled = true },
            multiplayerRealms = new { enabled = false },
            telemetry = new { enabled = false },
            optionalTelemetry = new { enabled = false },
        },
        profanityFilterPreferences = new { profanityFilterOn = false },
        banStatus = new { bannedScopes = new { } },
    });

    // ---------------------------------------------------------------- textures

    private static async Task<IResult> UploadTexture(
        string uuid, string textureType, HttpRequest request, TokenService tokens, SkinService skins, CancellationToken ct)
    {
        var profileId = await AuthorizeProfileAsync(uuid, textureType, request, tokens, ct);
        if (!request.HasFormContentType)
            throw YggdrasilException.IllegalArgument("Expected multipart/form-data.");

        var form = await request.ReadFormAsync(ct);
        if (form.Files.GetFile("file") is not { } file)
            throw YggdrasilException.IllegalArgument("Missing file.");
        if (file.Length > TextureImages.MaxUploadBytes)
            throw YggdrasilException.IllegalArgument("Файл слишком большой.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var model = form["model"].ToString() == "slim" ? SkinModel.Slim : SkinModel.Classic;
        if (await skins.SetSkinAsync(profileId, buffer.ToArray(), model, ct) is { } error)
            throw YggdrasilException.IllegalArgument(error);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTexture(
        string uuid, string textureType, HttpRequest request, TokenService tokens, SkinService skins, CancellationToken ct)
    {
        var profileId = await AuthorizeProfileAsync(uuid, textureType, request, tokens, ct);
        await skins.ResetSkinAsync(profileId, ct);
        return Results.NoContent();
    }

    /// <summary>Checks the Bearer token owns the profile; only skins can be changed by players.</summary>
    private static async Task<Guid> AuthorizeProfileAsync(
        string uuid, string textureType, HttpRequest request, TokenService tokens, CancellationToken ct)
    {
        var header = request.Headers.Authorization.ToString();
        var accessToken = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var token = await tokens.FindUsableAsync(accessToken, null, ct)
            ?? throw new YggdrasilException(StatusCodes.Status401Unauthorized, "UnauthorizedOperationException", "Invalid token.");

        if (!MinecraftUuid.TryParse(uuid, out var profileId) || token.ProfileId != profileId)
            throw YggdrasilException.Forbidden("Profile does not belong to this token.");
        if (textureType != "skin")
            throw YggdrasilException.Forbidden("Only skins can be changed.");
        return profileId;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Finds the account by e-mail or in-game name and checks the password.</summary>
    private static async Task<ApplicationUser> CheckCredentialsAsync(
        string? login, string? password, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, LumoraDbContext db)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            throw YggdrasilException.InvalidCredentials();
        login = login.Trim();

        ApplicationUser? user;
        if (login.Contains('@'))
            user = await users.FindByEmailAsync(login);
        else
        {
            var userId = await db.Profiles.Where(p => p.Name == login).Select(p => p.UserId).FirstOrDefaultAsync();
            user = userId is null ? null : await users.FindByIdAsync(userId);
        }
        if (user is null)
            throw YggdrasilException.InvalidCredentials();

        if (user.BanReason is not null && user.LockoutEnd > DateTimeOffset.UtcNow)
            throw YggdrasilException.Forbidden($"Аккаунт заблокирован: {user.BanReason}");

        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsLockedOut)
            throw YggdrasilException.Forbidden("Слишком много неудачных попыток входа. Попробуйте через несколько минут.");
        if (result.IsNotAllowed)
            throw YggdrasilException.Forbidden("Подтвердите e-mail: ссылка пришла в письме после регистрации.");
        if (!result.Succeeded)
            throw YggdrasilException.InvalidCredentials();
        return user;
    }

    private static UserDto UserOf(ApplicationUser user) =>
        new(user.Id.Replace("-", ""), [new PropertyDto("preferredLanguage", "ru")]);

    private static IResult Json(object value) => Results.Json(value, YggdrasilJson.Options);

    private static async ValueTask<object?> HandleErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (YggdrasilException e)
        {
            return Results.Json(new ErrorResponse(e.Error, e.Message), YggdrasilJson.Options, statusCode: e.Status);
        }
    }
}
