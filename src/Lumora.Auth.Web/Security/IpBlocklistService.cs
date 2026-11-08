using Lumora.Auth.Core.Security;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// Downloads public lists of malicious networks (FireHOL level1: Spamhaus DROP/EDROP, DShield, botnet C&amp;C …)
/// once a day and hands them to <see cref="IpGuard"/>. The last copy is cached on disk for offline starts.
/// Also reloads rules every minute so expired bans disappear.
/// </summary>
public sealed class IpBlocklistService(
    IpGuard guard, AutoBanService autoBan, IHttpClientFactory httpFactory, LumoraPaths paths,
    IOptions<LumoraOptions> options, ILogger<IpBlocklistService> logger) : BackgroundService
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

    private string CacheFile => Path.Combine(paths.Root, "blocklists", "combined.netset");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeAsync(() => guard.ReloadAsync(stoppingToken), "load IP rules");
        LoadCache();

        var nextDownload = guard.BlocklistUpdatedAt is { } at && DateTimeOffset.UtcNow - at < RefreshEvery
            ? at + RefreshEvery
            : DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextDownload)
            {
                await SafeAsync(() => DownloadAsync(stoppingToken), "download blocklists");
                nextDownload = DateTimeOffset.UtcNow + RefreshEvery;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SafeAsync(() => guard.ReloadAsync(stoppingToken), "reload IP rules");
            autoBan.Cleanup();
        }
    }

    private void LoadCache()
    {
        if (!File.Exists(CacheFile))
            return;
        var set = new IpRangeSet(IpRangeSet.ParseList(File.ReadAllText(CacheFile)));
        guard.SetBlocklist(set, File.GetLastWriteTimeUtc(CacheFile));
        logger.LogInformation("Loaded {Count} blocked ranges from cache", set.Count);
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        var urls = options.Value.Security.BlocklistUrls;
        if (urls.Count == 0)
            return;
        var http = httpFactory.CreateClient(nameof(IpBlocklistService));
        var texts = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                texts.Add(await http.GetStringAsync(url, ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning("Blocklist {Url} not available: {Message}", url, e.Message);
            }
        }
        if (texts.Count == 0)
            return;

        var combined = string.Join('\n', texts);
        var set = new IpRangeSet(IpRangeSet.ParseList(combined));
        if (set.Count == 0)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        await File.WriteAllTextAsync(CacheFile, combined, ct);
        guard.SetBlocklist(set, DateTimeOffset.UtcNow);
        logger.LogInformation("Blocklists updated: {Count} ranges", set.Count);
    }

    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to {What}", what);
        }
    }
}
