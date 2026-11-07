namespace Lumora.Contracts;

/// <summary>A game server as shown in the launcher (GET /api/launcher/servers).</summary>
public sealed record LauncherServerDto(
    Guid Id,
    string Name,
    string? Description,
    string Address,
    int Port,
    string? IconUrl,
    bool AddToServerList,
    bool AutoConnect,
    LauncherBuildSummaryDto Build);

/// <summary>Short build info embedded in the server list.</summary>
public sealed record LauncherBuildSummaryDto(
    Guid Id,
    string Name,
    int Revision,
    string MinecraftVersion,
    LoaderType Loader,
    string? LoaderVersion);

/// <summary>Full description of a build (GET /api/launcher/builds/{id}/manifest).</summary>
public sealed record BuildManifestDto(
    Guid Id,
    string Name,
    int Revision,
    string MinecraftVersion,
    LoaderType Loader,
    string? LoaderVersion,
    int JavaMajorVersion,
    int RecommendedMemoryMb,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<BuildFileDto> Files,
    IReadOnlyList<SyncRuleDto> SyncRules,
    IReadOnlyList<LauncherServerEntryDto> Servers);

/// <summary>A file the launcher must place into the instance folder.</summary>
public sealed record BuildFileDto(
    string Path,
    string Sha1,
    long Size,
    string Url,
    FileInstallMode Mode,
    string? DisplayName);

/// <summary>Sync policy for a directory relative to the instance root (e.g. "mods").</summary>
public sealed record SyncRuleDto(string Path, SyncPolicy Policy);

/// <summary>Entry written into servers.dat by the launcher.</summary>
public sealed record LauncherServerEntryDto(string Name, string Address, string? IconBase64);

/// <summary>authlib-injector download info (GET /api/launcher/authlib-injector).</summary>
public sealed record AuthlibInjectorDto(string Version, string Url, string Sha256);
