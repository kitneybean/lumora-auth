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
