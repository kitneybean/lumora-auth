using System.Net;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// The client address of a request. Behind a proxy, ForwardedHeaders middleware (Lumora:BehindProxy) has already
/// replaced RemoteIpAddress with the address from X-Forwarded-For, so this is the real visitor.
/// </summary>
public static class ClientIp
{
    public static IPAddress? Of(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        return ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
    }
}
