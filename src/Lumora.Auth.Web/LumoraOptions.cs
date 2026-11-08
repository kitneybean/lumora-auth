namespace Lumora.Auth.Web;

/// <summary>"Lumora" section of appsettings.</summary>
public sealed class LumoraOptions
{
    public const string Section = "Lumora";

    /// <summary>Name shown in launchers and authlib-injector metadata.</summary>
    public string ServerName { get; set; } = "Lumora";

    /// <summary>Public base URL of this site, used to build texture and file links.</summary>
    public string PublicUrl { get; set; } = "https://localhost:7443";

    /// <summary>Folder for the RSA key, textures and build files.</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>How long a launcher access token can be used to play.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(15);

    /// <summary>Extra time after expiry during which an expired token can still be refreshed.</summary>
    public TimeSpan TokenRefreshWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Older tokens of a user are revoked beyond this count.</summary>
    public int MaxTokensPerUser { get; set; } = 10;

    /// <summary>Running behind a reverse proxy: trust X-Forwarded-For/Proto/Host.</summary>
    public bool BehindProxy { get; set; }

    /// <summary>Skip migrations/seeding on startup (tests, design-time tools).</summary>
    public bool SkipDatabaseInit { get; set; }

    /// <summary>Optional first administrator, created on startup if missing.</summary>
    public AdminSeedOptions Admin { get; set; } = new();

    /// <summary>GitHub repository ("owner/name") whose latest release the download button points to; empty hides it.</summary>
    public string LauncherRepository { get; set; } = "kitneybean/lumora-launcher";

    /// <summary>GitHub repository ("owner/name") with Lumora Agent releases, used by the agent install script.</summary>
    public string AgentRepository { get; set; } = "kitneybean/lumora-gameserver";

    public SecurityOptions Security { get; set; } = new();
}

/// <summary>"Lumora:Security": protection against bots and password guessing.</summary>
public sealed class SecurityOptions
{
    /// <summary>Admins must sign in with two-factor authentication to open the admin panel.</summary>
    public bool RequireAdmin2fa { get; set; } = true;

    /// <summary>Public lists of malicious IPs (netset/CIDR per line), refreshed daily. Empty = off.</summary>
    public List<string> BlocklistUrls { get; set; } = ["https://iplists.firehol.org/files/firehol_level1.netset"];

    public bool AutoBan { get; set; } = true;

    /// <summary>Requests per minute per IP for pages and the site API (game files have their own limit).</summary>
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Downloads of build files and textures per minute per IP (a modpack has hundreds of files).</summary>
    public int FileRequestsPerMinute { get; set; } = 3000;

    /// <summary>Form posts to /Account/* (login, register, password reset) per minute per IP.</summary>
    public int AccountPostsPerMinute { get; set; } = 10;

    public int RegistrationsPerHour { get; set; } = 3;

    /// <summary>Failed logins in 10 minutes before an automatic ban.</summary>
    public int FailedLoginsBeforeBan { get; set; } = 10;

    /// <summary>Requests to typical vulnerability-scanner paths in 10 minutes before a ban.</summary>
    public int ScannerHitsBeforeBan { get; set; } = 3;

    /// <summary>Rate-limit rejections in 10 minutes before a ban.</summary>
    public int RateLimitHitsBeforeBan { get; set; } = 5;
}

public sealed class AdminSeedOptions
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}
