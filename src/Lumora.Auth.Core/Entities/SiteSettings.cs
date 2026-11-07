namespace Lumora.Auth.Core.Entities;

public enum SmtpSecurity
{
    /// <summary>Plain connection (only for local test servers).</summary>
    None,
    /// <summary>Upgrade with STARTTLS, usually port 587.</summary>
    StartTls,
    /// <summary>TLS from the start, usually port 465.</summary>
    SslOnConnect,
}

/// <summary>Site settings edited in the admin panel (single row, Id = 1).</summary>
public class SiteSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Project name shown on the site and in launchers.</summary>
    public string ServerName { get; set; } = "Lumora";

    public bool RegistrationOpen { get; set; } = true;

    /// <summary>New players must confirm their e-mail before logging in (needs working mail).</summary>
    public bool RequireConfirmedEmail { get; set; }

    public int TokenLifetimeDays { get; set; } = 15;

    public int MaxTokensPerUser { get; set; } = 10;

    // --- Mail ---

    public bool SmtpEnabled { get; set; }

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 465;

    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.SslOnConnect;

    public string? SmtpUsername { get; set; }

    /// <summary>Password encrypted with ASP.NET Data Protection (never stored in plain text).</summary>
    public string? SmtpPasswordProtected { get; set; }

    public string? MailFromAddress { get; set; }

    public string? MailFromName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SiteSettings Clone() => (SiteSettings)MemberwiseClone();
}
