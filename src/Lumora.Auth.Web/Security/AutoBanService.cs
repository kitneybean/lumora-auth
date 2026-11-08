using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// fail2ban-like counters: failed logins, scanner probes and rate-limit hits per IP in a 10-minute window.
/// Crossing a threshold bans the IP through <see cref="IpGuard"/>.
/// </summary>
public sealed class AutoBanService(IpGuard guard, IOptions<LumoraOptions> options, ILogger<AutoBanService> logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public enum Offence { FailedLogin, ScannerProbe, RateLimited }

    private readonly ConcurrentDictionary<(string Ip, Offence Kind), ConcurrentQueue<DateTimeOffset>> _hits = new();
    private readonly ConcurrentDictionary<string, byte> _banning = new();

    private SecurityOptions Options => options.Value.Security;

    public void Record(IPAddress? ip, Offence kind)
    {
        if (ip is null || !Options.AutoBan || guard.IsAllowListed(ip))
            return;

        var key = (ip.ToString(), kind);
        var queue = _hits.GetOrAdd(key, _ => new ConcurrentQueue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        queue.Enqueue(now);
        while (queue.TryPeek(out var first) && now - first > Window)
            queue.TryDequeue(out _);

        var threshold = kind switch
        {
            Offence.FailedLogin => Options.FailedLoginsBeforeBan,
            Offence.ScannerProbe => Options.ScannerHitsBeforeBan,
            _ => Options.RateLimitHitsBeforeBan,
        };
        if (queue.Count < threshold || !_banning.TryAdd(key.Item1, 0))
            return;

        _hits.TryRemove(key, out _);
        var reason = kind switch
        {
            Offence.FailedLogin => $"подбор пароля ({threshold} неудачных входов за 10 мин)",
            Offence.ScannerProbe => $"сканирование уязвимостей ({threshold} запроса к служебным путям)",
            _ => $"превышение лимита запросов ({threshold} раз за 10 мин)",
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await guard.AutoBanAsync(ip, reason);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Auto-ban of {Ip} failed", ip);
            }
            finally
            {
                _banning.TryRemove(key.Item1, out _);
            }
        });
    }

    /// <summary>Drops stale counters; called by the blocklist refresher.</summary>
    public void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, queue) in _hits)
        {
            if (!queue.TryPeek(out var last) || now - last > Window)
                _hits.TryRemove(key, out _);
        }
    }
}
