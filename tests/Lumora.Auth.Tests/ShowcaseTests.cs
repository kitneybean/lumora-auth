using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Lumora.Auth.Tests.Infrastructure;
using Lumora.Auth.Web.Showcase;
using Microsoft.Extensions.DependencyInjection;

namespace Lumora.Auth.Tests;

[Collection(AppCollection.Name)]
public class ShowcaseTests(LumoraAppFactory app)
{
    [Theory]
    [InlineData("Lumora-0.1.1-win-x64.zip", "windows")]
    [InlineData("Lumora-0.1.1-linux-x64.zip", "linux")]
    [InlineData("Lumora-0.1.1-osx-arm64.zip", "macos")]
    [InlineData("checksums.txt", null)]
    public void Release_files_are_matched_to_systems(string file, string? os) =>
        Assert.Equal(os, ShowcaseService.Classify(file).Os);

    [Fact]
    public async Task Landing_page_shows_servers_with_live_status()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fake = AnswerStatusAsync(listener, """{"version":{"name":"1.21.1","protocol":767},"players":{"max":20,"online":3},"description":{"text":"§aДобро пожаловать"}}""");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LumoraDbContext>();
            db.Servers.Add(new GameServer { Name = "Showcase Realm", Address = "127.0.0.1", Port = port, Description = "Ванильное выживание" });
            await db.SaveChangesAsync();
        }

        var html = await app.CreateClient().GetStringAsync("/");
        await fake;
        Assert.Contains("Showcase Realm", html);
        Assert.Contains("Ванильное выживание", html);
        Assert.Contains("3 / 20", html);
        Assert.Contains($"127.0.0.1:{port}", html);
    }

    /// <summary>Minimal Minecraft server: answers one Server List Ping.</summary>
    private static async Task AnswerStatusAsync(TcpListener listener, string json)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        await ReadPacketAsync(stream); // handshake
        await ReadPacketAsync(stream); // status request
        var body = Encoding.UTF8.GetBytes(json);
        var response = new MemoryStream();
        response.WriteByte(0x00);
        WriteVarInt(response, body.Length);
        response.Write(body);
        await WritePacketAsync(stream, response.ToArray());
        var ping = await ReadPacketAsync(stream);
        await WritePacketAsync(stream, ping);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream s)
    {
        int length = 0, shift = 0;
        var one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
                break;
            shift += 7;
        }
        var data = new byte[length];
        await s.ReadExactlyAsync(data);
        return data;
    }

    private static async Task WritePacketAsync(Stream s, byte[] payload)
    {
        var packet = new MemoryStream();
        WriteVarInt(packet, payload.Length);
        packet.Write(payload);
        await s.WriteAsync(packet.ToArray());
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
}
