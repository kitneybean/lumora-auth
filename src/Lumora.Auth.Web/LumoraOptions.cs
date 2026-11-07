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

    /// <summary>Skip migrations/seeding on startup (tests, design-time tools).</summary>
    public bool SkipDatabaseInit { get; set; }

    /// <summary>Optional first administrator, created on startup if missing.</summary>
    public AdminSeedOptions Admin { get; set; } = new();
}

public sealed class AdminSeedOptions
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}
