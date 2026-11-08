using System.Security.Claims;
using System.Threading.Channels;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Security;

/// <summary>
/// Security journal. Writes are queued and stored in the background so a request never waits for them;
/// events older than 90 days are removed daily.
/// </summary>
public sealed class AuditService(IDbContextFactory<LumoraDbContext> dbFactory, ILogger<AuditService> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly Channel<AuditEvent> _queue = Channel.CreateBounded<AuditEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Log(string action, bool success = true, string? userId = null, string? userName = null, string? ip = null, string? details = null)
    {
        _queue.Writer.TryWrite(new AuditEvent
        {
            Action = action,
            Success = success,
            UserId = userId,
            UserName = Trim(userName, 256),
            Ip = ip,
            Details = Trim(details, 2000),
        });
    }

    /// <summary>Logs with the user and IP of the current request.</summary>
    public void Log(HttpContext? http, string action, bool success = true, string? details = null, string? userName = null)
    {
        var user = http?.User;
        Log(action, success,
            user?.FindFirstValue(ClaimTypes.NameIdentifier),
            userName ?? user?.Identity?.Name,
            http is null ? null : ClientIp.Of(http)?.ToString(),
            details);
    }

    /// <summary>Logs from Blazor components, where only the user principal is at hand.</summary>
    public void Log(ClaimsPrincipal? user, string action, bool success = true, string? details = null) =>
        Log(action, success, user?.FindFirstValue(ClaimTypes.NameIdentifier), user?.Identity?.Name, null, details);

    /// <summary>Waits until queued events are stored (tests).</summary>
    public async Task FlushAsync()
    {
        for (var i = 0; i < 100 && _queue.Reader.Count > 0; i++)
            await Task.Delay(50);
        await Task.Delay(100);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.UtcNow;
        var batch = new List<AuditEvent>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.Reader.WaitToReadAsync(stoppingToken);
                while (batch.Count < 200 && _queue.Reader.TryRead(out var e))
                    batch.Add(e);

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                db.AuditEvents.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
                batch.Clear();

                if (DateTimeOffset.UtcNow >= nextCleanup)
                {
                    var cutoff = DateTimeOffset.UtcNow - Retention;
                    await db.AuditEvents.Where(a => a.At < cutoff).ExecuteDeleteAsync(stoppingToken);
                    nextCleanup = DateTimeOffset.UtcNow.AddDays(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to write audit events");
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
