using System.Net;
using System.Net.Sockets;

namespace Lumora.Auth.Core.Security;

/// <summary>
/// An IP address or CIDR subnet (IPv4 or IPv6). IPv4 is stored as IPv4-mapped IPv6 so both families
/// live in one 128-bit space.
/// </summary>
public readonly record struct IpNetwork(UInt128 Start, UInt128 End)
{
    public static bool TryParse(string? text, out IpNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        var slash = s.IndexOf('/');
        var addressPart = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addressPart, out var address))
            return false;

        var isV4 = address.AddressFamily == AddressFamily.InterNetwork;
        var maxPrefix = isV4 ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0 && (!int.TryParse(s[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix))
            return false;

        var value = ToUInt128(address);
        var hostBits = maxPrefix - prefix;
        var mask = hostBits == 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - 1;
        network = new IpNetwork(value & ~mask, value | mask);
        return true;
    }

    public static IpNetwork Parse(string text) =>
        TryParse(text, out var n) ? n : throw new FormatException($"Неверный IP или подсеть: {text}");

    public bool Contains(IPAddress address)
    {
        var v = ToUInt128(address);
        return v >= Start && v <= End;
    }

    /// <summary>128-bit value of an address; IPv4 (and IPv4-mapped IPv6) become ::ffff:a.b.c.d.</summary>
    public static UInt128 ToUInt128(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.AddressFamily == AddressFamily.InterNetwork
            ? address.MapToIPv6().GetAddressBytes()
            : address.GetAddressBytes();
        UInt128 value = 0;
        foreach (var b in bytes)
            value = (value << 8) | b;
        return value;
    }

    /// <summary>Loopback and private ranges (RFC 1918, ULA, link-local): never blocked.</summary>
    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT / Docker Desktop
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
    }
}

/// <summary>Large, read-only set of ranges (blocklists): sorted, merged, binary search.</summary>
public sealed class IpRangeSet
{
    private readonly UInt128[] _starts;
    private readonly UInt128[] _ends;

    public IpRangeSet(IEnumerable<IpNetwork> networks)
    {
        var merged = new List<IpNetwork>();
        foreach (var n in networks.OrderBy(n => n.Start))
        {
            if (merged.Count > 0 && n.Start <= merged[^1].End + 1 && merged[^1].End != UInt128.MaxValue)
            {
                if (n.End > merged[^1].End)
                    merged[^1] = merged[^1] with { End = n.End };
            }
            else
            {
                merged.Add(n);
            }
        }
        _starts = merged.Select(m => m.Start).ToArray();
        _ends = merged.Select(m => m.End).ToArray();
    }

    public static IpRangeSet Empty { get; } = new([]);

    public int Count => _starts.Length;

    public bool Contains(IPAddress address)
    {
        if (_starts.Length == 0)
            return false;
        var v = IpNetwork.ToUInt128(address);
        var i = Array.BinarySearch(_starts, v);
        if (i < 0)
            i = ~i - 1;
        return i >= 0 && v <= _ends[i];
    }

    /// <summary>Parses netset/DROP-style text: one IP or CIDR per line, comments after '#' or ';'.</summary>
    public static IEnumerable<IpNetwork> ParseList(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var cut = line.IndexOfAny(['#', ';']);
            if (cut >= 0)
                line = line[..cut];
            line = line.Trim();
            if (line.Length > 0 && IpNetwork.TryParse(line.Split(' ', '\t')[0], out var n))
                yield return n;
        }
    }
}
