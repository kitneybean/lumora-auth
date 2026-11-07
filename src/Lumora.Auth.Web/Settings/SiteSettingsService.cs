using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Settings;

/// <summary>
/// Site settings stored in the database and edited in the admin panel. Kept in memory;
/// changes apply immediately without a restart.
/// </summary>
public sealed class SiteSettingsService(
    IDbContextFactory<LumoraDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IOptions<LumoraOptions> options,
    IOptions<IdentityOptions> identity)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Lumora.SiteSettings.Smtp");
    private volatile SiteSettings _current = Defaults(options.Value);

    /// <summary>Copy of the current settings.</summary>
    public SiteSettings Current => _current.Clone();

    public event Action? Changed;

    /// <summary>Loads (or creates) the settings row; called once at startup.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SiteSettings.SingletonId, ct);
        if (row is null)
        {
            row = Defaults(options.Value);
            db.SiteSettings.Add(row);
            await db.SaveChangesAsync(ct);
        }
        Apply(row);
    }

    /// <summary>Saves settings. <paramref name="newSmtpPassword"/>: null keeps the stored one, "" clears it.</summary>
    public async Task SaveAsync(SiteSettings edited, string? newSmtpPassword, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Id == SiteSettings.SingletonId, ct);
        if (row is null)
        {
            row = Defaults(options.Value);
            db.SiteSettings.Add(row);
        }

        row.ServerName = string.IsNullOrWhiteSpace(edited.ServerName) ? "Lumora" : edited.ServerName.Trim();
        row.RegistrationOpen = edited.RegistrationOpen;
        row.RequireConfirmedEmail = edited.RequireConfirmedEmail;
        row.TokenLifetimeDays = Math.Clamp(edited.TokenLifetimeDays, 1, 365);
        row.MaxTokensPerUser = Math.Clamp(edited.MaxTokensPerUser, 1, 100);
        row.SmtpEnabled = edited.SmtpEnabled;
        row.SmtpHost = Trim(edited.SmtpHost);
        row.SmtpPort = Math.Clamp(edited.SmtpPort, 1, 65535);
        row.SmtpSecurity = edited.SmtpSecurity;
        row.SmtpUsername = Trim(edited.SmtpUsername);
        row.MailFromAddress = Trim(edited.MailFromAddress);
        row.MailFromName = Trim(edited.MailFromName);
        if (newSmtpPassword is not null)
            row.SmtpPasswordProtected = newSmtpPassword.Length == 0 ? null : _protector.Protect(newSmtpPassword);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Apply(row);
    }

    /// <summary>Decrypted SMTP password, or null.</summary>
    public string? SmtpPassword(SiteSettings settings)
    {
        if (settings.SmtpPasswordProtected is null)
            return null;
        try
        {
            return _protector.Unprotect(settings.SmtpPasswordProtected);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Data protection keys were lost: the password must be entered again.
            return null;
        }
    }

    public bool MailConfigured(SiteSettings s) =>
        s.SmtpEnabled && !string.IsNullOrWhiteSpace(s.SmtpHost) && !string.IsNullOrWhiteSpace(s.MailFromAddress);

    private void Apply(SiteSettings row)
    {
        _current = row.Clone();
        // Identity reads this flag on every sign-in; e-mail confirmation only makes sense with working mail.
        identity.Value.SignIn.RequireConfirmedAccount = row.RequireConfirmedEmail && MailConfigured(row);
        Changed?.Invoke();
    }

    private static SiteSettings Defaults(LumoraOptions o) => new()
    {
        ServerName = o.ServerName,
        TokenLifetimeDays = Math.Max(1, (int)o.TokenLifetime.TotalDays),
        MaxTokensPerUser = o.MaxTokensPerUser,
    };

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
