using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Auth.Web.Yggdrasil;

// Request and response shapes of the Yggdrasil API as used by authlib-injector.
// https://github.com/yushijinhun/authlib-injector/wiki/Yggdrasil-服务端技术规范

public sealed record AgentDto(string? Name, int Version);

public sealed record AuthenticateRequest(string? Username, string? Password, string? ClientToken, bool RequestUser, AgentDto? Agent);

public sealed record RefreshRequest(string? AccessToken, string? ClientToken, bool RequestUser, ProfileRef? SelectedProfile);

public sealed record TokenRequest(string? AccessToken, string? ClientToken);

public sealed record SignoutRequest(string? Username, string? Password);

public sealed record JoinRequest(string? AccessToken, string? SelectedProfile, string? ServerId);

/// <summary>Short profile: unsigned UUID and name.</summary>
public sealed record ProfileRef(string Id, string Name);

public sealed record PropertyDto(string Name, string Value, string? Signature = null);

public sealed record GameProfileDto(string Id, string Name, IReadOnlyList<PropertyDto> Properties);

public sealed record UserDto(string Id, IReadOnlyList<PropertyDto> Properties);

public sealed record AuthenticateResponse(
    string AccessToken,
    string ClientToken,
    IReadOnlyList<ProfileRef> AvailableProfiles,
    ProfileRef? SelectedProfile,
    UserDto? User);

public sealed record RefreshResponse(string AccessToken, string ClientToken, ProfileRef? SelectedProfile, UserDto? User);

public sealed record ErrorResponse(string Error, string ErrorMessage, string? Cause = null);

public static class YggdrasilJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Error that maps to a Yggdrasil error response.</summary>
public sealed class YggdrasilException(int status, string error, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;

    public static YggdrasilException InvalidToken() =>
        new(StatusCodes.Status403Forbidden, "ForbiddenOperationException", "Invalid token.");

    public static YggdrasilException InvalidCredentials() =>
        new(StatusCodes.Status403Forbidden, "ForbiddenOperationException", "Invalid credentials. Invalid username or password.");

    public static YggdrasilException Forbidden(string message) =>
        new(StatusCodes.Status403Forbidden, "ForbiddenOperationException", message);

    public static YggdrasilException IllegalArgument(string message) =>
        new(StatusCodes.Status400BadRequest, "IllegalArgumentException", message);
}
