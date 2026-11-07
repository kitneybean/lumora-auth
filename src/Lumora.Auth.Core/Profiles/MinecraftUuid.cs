namespace Lumora.Auth.Core.Profiles;

/// <summary>Helpers for the UUID format used by Yggdrasil (32 hex chars, no dashes).</summary>
public static class MinecraftUuid
{
    public static string ToUnsigned(Guid id) => id.ToString("N");

    public static bool TryParse(string? value, out Guid id) =>
        Guid.TryParseExact(value, "N", out id) || Guid.TryParseExact(value, "D", out id);
}
