using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace Lumora.Auth.Web.Launcher;

/// <summary>
/// Suggestions for the admin build form: Minecraft releases (Mojang) and NeoForge versions (NeoForged maven).
/// Failures return empty lists — the admin can still type versions by hand.
/// </summary>
public sealed class GameVersionsService(IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<GameVersionsService> logger)
{
    public const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string NeoForgeMetadata = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    private sealed record Manifest([property: JsonPropertyName("versions")] List<Version> Versions);
    private sealed record Version([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("type")] string Type);

    public Task<IReadOnlyList<string>> MinecraftReleasesAsync(CancellationToken ct = default) =>
        CachedAsync("mc-releases", async () =>
        {
            var manifest = await Http().GetFromJsonAsync<Manifest>(MojangManifest, ct);
            return (IReadOnlyList<string>)(manifest?.Versions.Where(v => v.Type == "release").Select(v => v.Id).ToList() ?? []);
        });

    /// <summary>NeoForge versions for a Minecraft version, newest first (1.21.1 → 21.1.*).</summary>
    public async Task<IReadOnlyList<string>> NeoForgeVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var all = await CachedAsync("neoforge", async () =>
        {
            var xml = XDocument.Parse(await Http().GetStringAsync(NeoForgeMetadata, ct));
            return (IReadOnlyList<string>)xml.Descendants("version").Select(v => v.Value).ToList();
        });
        var prefix = NeoForgePrefix(minecraftVersion);
        return prefix is null ? [] : all.Where(v => v.StartsWith(prefix, StringComparison.Ordinal)).Reverse().ToList();
    }

    /// <summary>"1.21.1" → "21.1.", "1.21" → "21.0.".</summary>
    public static string? NeoForgePrefix(string minecraftVersion)
    {
        var parts = minecraftVersion.Trim().Split('.');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out var minor))
            return null;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return $"{minor}.{patch}.";
    }

    private HttpClient Http() => httpFactory.CreateClient(nameof(GameVersionsService));

    private async Task<IReadOnlyList<string>> CachedAsync(string key, Func<Task<IReadOnlyList<string>>> load)
    {
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
            return cached;
        try
        {
            var value = await load();
            cache.Set(key, value, TimeSpan.FromHours(1));
            return value;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Xml.XmlException or System.Text.Json.JsonException)
        {
            logger.LogWarning(e, "Could not load versions ({Key})", key);
            return [];
        }
    }
}
