namespace Lumora.Contracts;

/// <summary>Mod loader used by a build.</summary>
public enum LoaderType
{
    Vanilla,
    NeoForge,
    Forge,
    Fabric,
}

/// <summary>Where a build file is needed.</summary>
public enum FileSide
{
    Both,
    Client,
    Server,
}

/// <summary>Whether a player may toggle a build file in the launcher.</summary>
public enum FileInstallMode
{
    /// <summary>Always installed, cannot be disabled.</summary>
    Required,
    /// <summary>Optional, enabled by default.</summary>
    OptionalOn,
    /// <summary>Optional, disabled by default.</summary>
    OptionalOff,
}

/// <summary>How the launcher treats a directory inside the game folder during sync.</summary>
public enum SyncPolicy
{
    /// <summary>Directory content must match the manifest exactly; unknown files are deleted.</summary>
    Strict,
    /// <summary>Manifest files are added/updated, user files are kept.</summary>
    AddOrUpdate,
    /// <summary>Files are written only if missing (e.g. default options.txt).</summary>
    CreateIfMissing,
}
