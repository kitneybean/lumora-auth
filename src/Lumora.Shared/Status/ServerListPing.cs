using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumora.Shared.Status;

/// <summary>
/// Minecraft "Server List Ping" (the protocol the multiplayer screen uses): handshake, status request,
/// then ping/pong to measure latency.
/// </summary>
public sealed class ServerListPing : IServerStatusService
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);

    public async Task<ServerStatus> GetStatusAsync(string address, int port, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            return await PingAsync(address, port, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or JsonException or InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            return ServerStatus.Offline;
        }
    }

    private static async Task<ServerStatus> PingAsync(string address, int port, CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(address, port, ct);
        var stream = client.GetStream();

        // Handshake (protocol -1 = "just asking"), next state 1 = status.
        var handshake = new MemoryStream();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1);
        WriteString(handshake, address);
        handshake.Write([(byte)(port >> 8), (byte)port]);
        WriteVarInt(handshake, 1);
        await SendPacketAsync(stream, handshake.ToArray(), ct);
        await SendPacketAsync(stream, [0x00], ct);

        var response = await ReadPacketAsync(stream, ct);
        var offset = 0;
        if (ReadVarInt(response, ref offset) != 0x00)
            throw new InvalidDataException("Unexpected status packet");
        var length = ReadVarInt(response, ref offset);
        var json = Encoding.UTF8.GetString(response, offset, length);

        // Ping: echo of a long payload.
        var ping = new byte[9];
        ping[0] = 0x01;
        BinaryPrimitives.WriteInt64BigEndian(ping.AsSpan(1), Environment.TickCount64);
        var sw = Stopwatch.StartNew();
        await SendPacketAsync(stream, ping, ct);
        int pingMs;
        try
        {
            await ReadPacketAsync(stream, ct);
            pingMs = (int)sw.ElapsedMilliseconds;
        }
        catch (IOException)
        {
            pingMs = (int)sw.ElapsedMilliseconds; // some servers close instead of answering the ping
        }

        return Parse(json, pingMs);
    }

    public static ServerStatus Parse(string json, int pingMs)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int online = 0, max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            online = players.TryGetProperty("online", out var o) ? o.GetInt32() : 0;
            max = players.TryGetProperty("max", out var m) ? m.GetInt32() : 0;
        }
        string? motd = null;
        if (root.TryGetProperty("description", out var description))
            motd = PlainText(description);
        return new ServerStatus(true, online, max, pingMs, motd);
    }

    /// <summary>Flattens a chat component (string or {text, extra}) and strips § formatting codes.</summary>
    private static string PlainText(JsonElement component)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    sb.Append(e.GetString());
                    break;
                case JsonValueKind.Object:
                    if (e.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                    if (e.TryGetProperty("extra", out var extra))
                        Walk(extra);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
        Walk(component);
        var s = sb.ToString();
        var clean = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '§' && i + 1 < s.Length)
                i++;
            else
                clean.Append(s[i]);
        }
        return clean.ToString().Trim();
    }

    private static async Task SendPacketAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await stream.WriteAsync(packet.ToArray(), ct);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length is <= 0 or > 1 << 21)
            throw new InvalidDataException("Bad packet length");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
                b |= 0x80;
            s.WriteByte(b);
        } while (v != 0);
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes);
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        int value = 0, shift = 0;
        while (true)
        {
            var b = data[offset++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt too long");
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        int value = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct);
            value |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt too long");
        }
    }
}
