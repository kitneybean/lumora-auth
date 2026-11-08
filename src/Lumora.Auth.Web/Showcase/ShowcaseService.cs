using System.Text.Json;
using Lumora.Auth.Data;
using Lumora.Shared.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Showcase;

public sealed record ShowcaseServer(Guid Id, string Name, string? Description, string Address, bool HasIcon,
    string? GameVersion, string? BuildName, ServerStatus Status);

public sealed record LauncherDownload(string Os, string Label, string Url, long Size);

public sealed record LauncherRelease(string Version, string PageUrl, DateTimeOffset PublishedAt, IReadOnlyList<LauncherDownload> Downloads);

/// <summary>
/// Data for the public landing page: servers with live status (Server List Ping, cached 30 s)
/// and the latest launcher release from GitHub (cached 1 h).
/// </summary>
public sealed class ShowcaseService(
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    IHttpClientFactory httpFactory,
    IOptions<LumoraOptions> options,
    ILogger<ShowcaseService> logger)
{
    private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(30);
    private readonly IServerStatusService _ping = new ServerListPing { Timeout = TimeSpan.FromSeconds(2.5) };

    public async Task<IReadOnlyList<ShowcaseServer>> GetServersAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LumoraDbContext>();
        var servers = await db.Servers.AsNoTracking()
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.Description, s.Address, s.Port,
                HasIcon = s.IconBase64 != null,
                GameVersion = s.GameBuild != null && s.GameBuild.IsPublished ? s.GameBuild.MinecraftVersion : null,
                BuildName = s.GameBuild != null && s.GameBuild.IsPublished ? s.GameBuild.Name : null,
            })
            .ToListAsync(ct);

        var statuses = await Task.WhenAll(servers.Select(s => StatusAsync(s.Address, s.Port, ct)));
        return servers.Select((s, i) => new ShowcaseServer(s.Id, s.Name, s.Description,
            s.Port == 25565 ? s.Address : $"{s.Address}:{s.Port}", s.HasIcon, s.GameVersion, s.BuildName, statuses[i])).ToList();
    }

    private Task<ServerStatus> StatusAsync(string address, int port, CancellationToken ct) =>
        cache.GetOrCreateAsync($"slp:{address}:{port}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = StatusTtl;
            return await _ping.GetStatusAsync(address, port, ct);
        })!;

    public async Task<LauncherRelease?> GetLauncherReleaseAsync(CancellationToken ct = default)
    {
        var repo = options.Value.LauncherRepository;
        if (string.IsNullOrWhiteSpace(repo))
            return null;
        return await cache.GetOrCreateAsync($"launcher-release:{repo}", async entry =>
        {
            try
            {
                var release = await FetchReleaseAsync(repo, ct);
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                return release;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                logger.LogWarning("Не удалось получить релиз лаунчера с GitHub: {Message}", e.Message);
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return null;
            }
        });
    }

    private async Task<LauncherRelease?> FetchReleaseAsync(string repo, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(ShowcaseService));
        using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        if (!response.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var downloads = new List<LauncherDownload>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var size = asset.GetProperty("size").GetInt64();
            if (!url.StartsWith("https://github.com/", StringComparison.Ordinal))
                continue;
            var (os, label) = Classify(name);
            if (os is not null)
                downloads.Add(new LauncherDownload(os, label!, url, size));
        }
        return new LauncherRelease(tag.TrimStart('v'), root.GetProperty("html_url").GetString() ?? $"https://github.com/{repo}/releases",
            root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.UtcNow,
            PreferInstallers(downloads).OrderBy(d => d.Os switch { "windows" => 0, "linux" => 1, _ => 2 }).ThenBy(d => d.Label).ToList());
    }

    /// <summary>When a system has an installer, players get it instead of the bare archive (shortcuts, uninstall entry).</summary>
    public static IEnumerable<LauncherDownload> PreferInstallers(IReadOnlyList<LauncherDownload> downloads) =>
        downloads.Where(d => !IsArchive(d.Url) || !downloads.Any(o => o.Os == d.Os && o.Label == d.Label && !IsArchive(o.Url)));

    private static bool IsArchive(string url) =>
        url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps release file names like "Lumora-0.1.1-win-x64.zip" or "Lumora-Setup-0.1.3-win-x64.exe" to an OS.</summary>
    public static (string? Os, string? Label) Classify(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (!n.EndsWith(".zip") && !n.EndsWith(".exe") && !n.EndsWith(".msi") && !n.EndsWith(".tar.gz") && !n.EndsWith(".dmg") && !n.EndsWith(".appimage"))
            return (null, null);
        if (n.Contains("win"))
            return ("windows", "Windows");
        if (n.Contains("linux"))
            return ("linux", "Linux");
        if (n.Contains("osx") || n.Contains("mac"))
            return ("macos", n.Contains("arm64") ? "macOS (Apple Silicon)" : "macOS (Intel)");
        return (null, null);
    }
}
