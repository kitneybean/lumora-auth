using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Auth.Web.Launcher;

/// <summary>
/// Keeps a copy of the authlib-injector jar (unmodified, AGPL-3.0) for launchers and game servers.
/// Downloaded from the official mirror on first use and checked against its SHA-256.
/// </summary>
public sealed class AuthlibInjectorProvider(LumoraPaths paths, IHttpClientFactory httpFactory, ILogger<AuthlibInjectorProvider> logger)
{
    public const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

    public sealed record Info(string Version, string Sha256, DateTimeOffset DownloadedAt);

    private sealed record Latest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("download_url")] string DownloadUrl,
        [property: JsonPropertyName("checksums")] Dictionary<string, string> Checksums);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string JarPath => Path.Combine(paths.AuthlibInjector, "authlib-injector.jar");
    private string InfoPath => Path.Combine(paths.AuthlibInjector, "info.json");

    /// <summary>Cached jar info, downloading it first if needed. Null when unavailable.</summary>
    public async Task<Info?> GetAsync(CancellationToken ct = default)
    {
        if (ReadInfo() is { } info && File.Exists(JarPath))
            return info;
        return await UpdateAsync(ct);
    }

    public Info? ReadInfo()
    {
        try
        {
            return File.Exists(InfoPath) ? JsonSerializer.Deserialize<Info>(File.ReadAllText(InfoPath)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Downloads the latest release. Returns null (and logs) on failure, keeping the old copy.</summary>
    public async Task<Info?> UpdateAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(nameof(AuthlibInjectorProvider));
            var latest = await http.GetFromJsonAsync<Latest>(LatestUrl, ct)
                ?? throw new InvalidOperationException("Empty response");
            if (ReadInfo() is { } current && current.Version == latest.Version && File.Exists(JarPath))
                return current;

            var bytes = await http.GetByteArrayAsync(latest.DownloadUrl, ct);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!latest.Checksums.TryGetValue("sha256", out var expected) || !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Checksum mismatch for authlib-injector {latest.Version}");

            Directory.CreateDirectory(paths.AuthlibInjector);
            await File.WriteAllBytesAsync(JarPath + ".tmp", bytes, ct);
            File.Move(JarPath + ".tmp", JarPath, overwrite: true);
            var info = new Info(latest.Version, sha256, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(InfoPath, JsonSerializer.Serialize(info), ct);
            logger.LogInformation("authlib-injector {Version} downloaded", latest.Version);
            return info;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(e, "Could not download authlib-injector");
            return ReadInfo() is { } old && File.Exists(JarPath) ? old : null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
