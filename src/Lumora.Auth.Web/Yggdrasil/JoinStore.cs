using Microsoft.Extensions.Caching.Memory;

namespace Lumora.Auth.Web.Yggdrasil;

/// <summary>
/// Short-lived records of "player X is joining server Y" made by the client (join)
/// and checked by the game server (hasJoined).
/// </summary>
public sealed class JoinStore(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public sealed record Entry(Guid ProfileId, string? Ip);

    public void Add(string serverId, Guid profileId, string? ip) =>
        cache.Set(Key(serverId), new Entry(profileId, ip), Lifetime);

    public Entry? Find(string serverId) => cache.Get<Entry>(Key(serverId));

    private static string Key(string serverId) => "yggdrasil-join:" + serverId;
}
