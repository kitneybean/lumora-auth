using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Tests.Infrastructure;
using Lumora.Auth.Web.Builds;
using Lumora.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Lumora.Auth.Tests;

[Collection(AppCollection.Name)]
public class LauncherApiTests(LumoraAppFactory app)
{
    private readonly HttpClient _http = app.CreateClient();

    [Fact]
    public async Task Build_is_visible_to_launchers_only_after_publish_and_as_a_snapshot()
    {
        var client = await AuthorizedClientAsync("Builder1");
        var (buildId, serverId) = await CreateBuildWithServerAsync("Snapshot pack", "snap.example.com");

        // Draft: not listed, no manifest.
        Assert.DoesNotContain(await ServersAsync(client), s => s.Id == serverId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/launcher/builds/{buildId}/manifest")).StatusCode);

        await WithServices<BuildService>(b => b.PublishAsync(buildId));

        var server = Assert.Single(await ServersAsync(client), s => s.Id == serverId);
        Assert.Equal(1, server.Build.Revision);
        Assert.Equal("1.21.1", server.Build.MinecraftVersion);
        Assert.Equal(LoaderType.NeoForge, server.Build.Loader);

        var manifest = await ManifestAsync(client, buildId);
        Assert.Equal(1, manifest.Revision);
        Assert.Equal(["config/a.toml", "mods/a.jar"], manifest.Files.Select(f => f.Path).ToArray());
        var jar = manifest.Files.Single(f => f.Path == "mods/a.jar");
        Assert.Equal(Sha1("jar-a"), jar.Sha1);
        Assert.Equal($"{LumoraAppFactory.PublicUrl}/api/launcher/files/{jar.Sha1}", jar.Url);
        Assert.Equal("snap.example.com", Assert.Single(manifest.Servers).Address);
        Assert.Contains(manifest.SyncRules, r => r.Path == "mods" && r.Policy == SyncPolicy.Strict);
        Assert.Equal(3, manifest.SyncRules.Count); // defaults, loaded once (regression: EF appended to pre-filled list)

        // Edits after publishing stay invisible until the next publish.
        await WithServices<BuildService>(b => b.AddFileAsync(buildId, "mods/b.jar", Bytes("jar-b")));
        Assert.Equal(2, (await ManifestAsync(client, buildId)).Files.Count);
        await WithServices<BuildService>(b => b.PublishAsync(buildId));
        var updated = await ManifestAsync(client, buildId);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(3, updated.Files.Count);
    }

    [Fact]
    public async Task Server_only_files_and_disabled_servers_are_hidden()
    {
        var client = await AuthorizedClientAsync("Builder2");
        var (buildId, serverId) = await CreateBuildWithServerAsync("Sides pack", "sides.example.com");
        await WithServices<BuildService>(async b =>
        {
            await b.AddFileAsync(buildId, "mods/server-only.jar", Bytes("server"));
            var build = await b.GetAsync(buildId);
            var file = build!.Files.Single(f => f.Path == "mods/server-only.jar");
            await b.UpdateFileAsync(file.Id, FileSide.Server, FileInstallMode.Required, null);
            await b.PublishAsync(buildId);
        });

        Assert.DoesNotContain((await ManifestAsync(client, buildId)).Files, f => f.Path == "mods/server-only.jar");

        await WithServices<ServerService>(async s =>
        {
            var server = (await s.ListAsync()).Single(x => x.Id == serverId);
            server.IsEnabled = false;
            Assert.Null(await s.SaveAsync(server));
        });
        Assert.DoesNotContain(await ServersAsync(client), s => s.Id == serverId);
    }

    [Fact]
    public async Task Files_need_a_token_and_match_their_hash()
    {
        var client = await AuthorizedClientAsync("Downloader");
        var (buildId, _) = await CreateBuildWithServerAsync("Files pack", "files.example.com");
        await WithServices<BuildService>(b => b.PublishAsync(buildId));
        var sha1 = Sha1("jar-a");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync($"/api/launcher/files/{sha1}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/launcher/servers")).StatusCode);

        var bytes = await client.GetByteArrayAsync($"/api/launcher/files/{sha1}");
        Assert.Equal("jar-a", Encoding.UTF8.GetString(bytes));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/launcher/files/{new string('0', 40)}")).StatusCode);
    }

    [Fact]
    public async Task Zip_upload_keeps_folders_and_skips_unsafe_paths()
    {
        var buildId = await WithServices<BuildService, Guid>(b => b.CreateAsync("Zip pack", "1.21.1", LoaderType.NeoForge, "21.1.77"));
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "mods/x.jar", "x");
            Add(archive, "config/x/x.toml", "cfg");
            Add(archive, "../evil.txt", "no");
            Add(archive, "saves/world/level.dat", "no");
        }
        zip.Position = 0;

        var result = await WithServices<BuildService, BuildService.AddResult>(b => b.AddZipAsync(buildId, zip, null));

        Assert.Equal(2, result.Added);
        Assert.Equal(2, result.Skipped.Count);
        var build = await WithServices<BuildService, GameBuild?>(b => b.GetAsync(buildId));
        Assert.Equal(["config/x/x.toml", "mods/x.jar"], build!.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task Mrpack_sets_versions_imports_overrides_and_skips_untrusted_downloads()
    {
        var buildId = await WithServices<BuildService, Guid>(b => b.CreateAsync("Mrpack", "1.20.1", LoaderType.Vanilla, null));
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "modrinth.index.json", """
                {"formatVersion":1,"game":"minecraft","versionId":"1.0","name":"Test Pack",
                 "files":[{"path":"mods/evil.jar","hashes":{"sha1":"00"},"env":{"client":"required","server":"required"},
                           "downloads":["https://evil.example.com/evil.jar"],"fileSize":1}],
                 "dependencies":{"minecraft":"1.21.1","neoforge":"21.1.77"}}
                """);
            Add(archive, "overrides/config/pack.toml", "both");
            Add(archive, "client-overrides/options.txt", "client");
            Add(archive, "server-overrides/config/server-only.toml", "server");
        }
        zip.Position = 0;

        var result = await WithServices<BuildService, BuildService.ImportResult>(b => b.ImportArchiveAsync(buildId, zip, null, null));

        Assert.Equal(3, result.Added);
        Assert.Contains(result.Skipped, s => s.Contains("mods/evil.jar"));
        Assert.Contains(result.Notes, n => n.Contains("Test Pack"));
        var build = await WithServices<BuildService, GameBuild?>(b => b.GetAsync(buildId));
        Assert.Equal(("1.21.1", LoaderType.NeoForge, "21.1.77"), (build!.MinecraftVersion, build.Loader, build.LoaderVersion));
        Assert.Equal(FileSide.Client, build.Files.Single(f => f.Path == "options.txt").Side);
        Assert.Equal(FileSide.Server, build.Files.Single(f => f.Path == "config/server-only.toml").Side);
        Assert.Equal(FileSide.Both, build.Files.Single(f => f.Path == "config/pack.toml").Side);
    }

    /// <summary>Downloads a real mod from Modrinth. Runs only with LUMORA_NETWORK_TESTS=1.</summary>
    [Fact]
    public async Task Mrpack_downloads_mods_from_modrinth()
    {
        if (Environment.GetEnvironmentVariable("LUMORA_NETWORK_TESTS") != "1")
            return;
        var buildId = await WithServices<BuildService, Guid>(b => b.CreateAsync("Modrinth", "1.21.1", LoaderType.NeoForge, "21.1.77"));
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "modrinth.index.json", """
                {"formatVersion":1,"game":"minecraft","versionId":"1","name":"Net",
                 "files":[{"path":"mods/MouseTweaks.jar","hashes":{"sha1":"6dae57f4f50f7808d2ed9a18f6cd1c0d4640c6bc"},
                           "env":{"client":"optional","server":"unsupported"},
                           "downloads":["https://cdn.modrinth.com/data/aC3cM3Vq/versions/9I21YYxf/MouseTweaks-neoforge-mc1.21-2.26.1.jar"],"fileSize":73938}],
                 "dependencies":{"minecraft":"1.21.1","neoforge":"21.1.77"}}
                """);
        }
        zip.Position = 0;

        var result = await WithServices<BuildService, BuildService.ImportResult>(b => b.ImportArchiveAsync(buildId, zip, null, null));

        Assert.Empty(result.Skipped);
        var file = (await WithServices<BuildService, GameBuild?>(b => b.GetAsync(buildId)))!.Files.Single();
        Assert.Equal(("6dae57f4f50f7808d2ed9a18f6cd1c0d4640c6bc", FileSide.Client, FileInstallMode.OptionalOn), (file.Sha1, file.Side, file.Mode));
        var report = await WithServices<BuildService, BuildService.ModReport>(b => b.AnalyzeModsAsync(buildId));
        Assert.Equal("mousetweaks", report.ModsBySha1.Values.Single().ModId);
    }

    [Fact]
    public async Task Zip_from_prism_export_strips_the_minecraft_folder_and_mods_are_checked()
    {
        var buildId = await WithServices<BuildService, Guid>(b => b.CreateAsync("Prism zip", "1.21.1", LoaderType.NeoForge, "21.1.77"));
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "instance.cfg", "x");
            using (var e = archive.CreateEntry(".minecraft/mods/create.jar").Open())
                ModCheckTests.Jar("[[mods]]\nmodId=\"create\"\n[[dependencies.create]]\nmodId=\"flywheel\"\ntype=\"required\"\n").CopyTo(e);
            Add(archive, ".minecraft/config/create-client.toml", "cfg");
        }
        zip.Position = 0;

        var result = await WithServices<BuildService, BuildService.ImportResult>(b => b.ImportArchiveAsync(buildId, zip, null, null));
        Assert.Equal(2, result.Added);

        var build = await WithServices<BuildService, GameBuild?>(b => b.GetAsync(buildId));
        Assert.Equal(["config/create-client.toml", "mods/create.jar"], build!.Files.Select(f => f.Path).ToArray());

        var report = await WithServices<BuildService, BuildService.ModReport>(b => b.AnalyzeModsAsync(buildId));
        Assert.Contains(report.Problems, p => p.Kind == "missing" && p.Message.Contains("flywheel"));
    }

    [Fact]
    public async Task Server_icon_is_served_and_linked()
    {
        var client = await AuthorizedClientAsync("IconViewer");
        var (buildId, serverId) = await CreateBuildWithServerAsync("Icon pack", "icon.example.com");
        await WithServices<ServerService>(async s =>
        {
            var server = (await s.ListAsync()).Single(x => x.Id == serverId);
            server.IconBase64 = ServerService.MakeIcon(TestImages.Png(200, 100));
            Assert.Null(await s.SaveAsync(server));
        });
        await WithServices<BuildService>(b => b.PublishAsync(buildId));

        var iconUrl = (await ServersAsync(client)).Single(s => s.Id == serverId).IconUrl;
        Assert.NotNull(iconUrl);
        var icon = await _http.GetAsync(new Uri(iconUrl).PathAndQuery);
        Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
        using var bitmap = SkiaSharp.SKBitmap.Decode(await icon.Content.ReadAsByteArrayAsync());
        Assert.Equal((64, 64), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public async Task Authlib_injector_is_served_from_the_local_copy()
    {
        var dir = Path.Combine(app.DataPath, "authlib-injector");
        Directory.CreateDirectory(dir);
        var jar = Bytes("fake jar").ToArray();
        await File.WriteAllBytesAsync(Path.Combine(dir, "authlib-injector.jar"), jar);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(jar));
        await File.WriteAllTextAsync(Path.Combine(dir, "info.json"),
            JsonSerializer.Serialize(new { Version = "9.9.9", Sha256 = sha256, DownloadedAt = DateTimeOffset.UtcNow }));

        var info = await _http.GetFromJsonAsync<AuthlibInjectorDto>("/api/launcher/authlib-injector", LumoraJson.Options);
        Assert.Equal("9.9.9", info!.Version);
        Assert.Equal(sha256, info.Sha256);
        Assert.Equal(jar, await _http.GetByteArrayAsync(new Uri(info.Url).PathAndQuery));
    }

    // ------------------------------------------------------------------

    private async Task<(Guid BuildId, Guid ServerId)> CreateBuildWithServerAsync(string name, string address)
    {
        var buildId = await WithServices<BuildService, Guid>(async b =>
        {
            var id = await b.CreateAsync(name, "1.21.1", LoaderType.NeoForge, "21.1.77");
            await b.AddFileAsync(id, "mods/a.jar", Bytes("jar-a"));
            await b.AddFileAsync(id, "config/a.toml", Bytes("cfg-a"));
            return id;
        });
        var server = new GameServer { Name = name, Address = address, GameBuildId = buildId };
        await WithServices<ServerService>(async s => Assert.Null(await s.SaveAsync(server)));
        return (buildId, server.Id);
    }

    private async Task<HttpClient> AuthorizedClientAsync(string player)
    {
        await app.CreatePlayerAsync(player);
        var response = await _http.PostAsJsonAsync("/api/yggdrasil/authserver/authenticate", new { username = player, password = "Password123" });
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<List<LauncherServerDto>> ServersAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<LauncherServerDto>>("/api/launcher/servers", LumoraJson.Options))!;

    private static async Task<BuildManifestDto> ManifestAsync(HttpClient client, Guid buildId) =>
        (await client.GetFromJsonAsync<BuildManifestDto>($"/api/launcher/builds/{buildId}/manifest", LumoraJson.Options))!;

    private async Task WithServices<T>(Func<T, Task> action) where T : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    private async Task<TResult> WithServices<T, TResult>(Func<T, Task<TResult>> action) where T : notnull
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<T>());
    }

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Sha1(string text) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Add(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(content);
    }
}
