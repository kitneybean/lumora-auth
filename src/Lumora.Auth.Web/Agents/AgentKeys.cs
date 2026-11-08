using System.Security.Cryptography;
using System.Text;

namespace Lumora.Auth.Web.Agents;

/// <summary>Agent keys and join tokens: random secrets stored on the site only as SHA-256.</summary>
public static class AgentKeys
{
    public const string KeyPrefix = "lma_";
    public const string TokenPrefix = "lmj_";

    public static string NewKey() => KeyPrefix + Random(32);

    public static string NewJoinToken() => TokenPrefix + Random(24);

    public static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Random(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
