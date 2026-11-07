using System.Text.RegularExpressions;

namespace Lumora.Auth.Core.Profiles;

/// <summary>Validation rules for in-game player names.</summary>
public static partial class PlayerName
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex ValidName();

    public static bool IsValid(string? name) => name is not null && ValidName().IsMatch(name);
}
