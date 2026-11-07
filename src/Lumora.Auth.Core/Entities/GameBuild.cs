using Lumora.Contracts;

namespace Lumora.Auth.Core.Entities;

/// <summary>A modpack ("сборка"): Minecraft version + loader + files.</summary>
public class GameBuild
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Minecraft version id, e.g. "1.21.1".</summary>
    public required string MinecraftVersion { get; set; }

    public LoaderType Loader { get; set; } = LoaderType.NeoForge;

    /// <summary>Loader version, e.g. "21.1.77". Null for vanilla.</summary>
    public string? LoaderVersion { get; set; }

    public int JavaMajorVersion { get; set; } = 21;

    public int RecommendedMemoryMb { get; set; } = 4096;

    /// <summary>JVM arguments forced by the build (one per entry).</summary>
    public List<string> JvmArguments { get; set; } = [];

    /// <summary>Incremented on every publish so launchers know they must resync.</summary>
    public int Revision { get; set; }

    public bool IsPublished { get; set; }

    /// <summary>When the current revision was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Manifest frozen at publish time (JSON of <see cref="BuildManifestDto"/> without URLs and servers).
    /// Launchers get this snapshot, so edits in progress are not visible until the next publish.
    /// </summary>
    public string? PublishedManifestJson { get; set; }

    /// <summary>True when the build was changed after the last publish.</summary>
    public bool HasUnpublishedChanges => PublishedAt is null || UpdatedAt > PublishedAt;

    public List<BuildFile> Files { get; set; } = [];

    // Empty by default: EF appends stored rules when loading, so defaults must not be pre-filled here.
    public List<SyncRule> SyncRules { get; set; } = [];

    public List<GameServer> Servers { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static List<SyncRule> DefaultSyncRules() =>
    [
        new() { Path = "mods", Policy = SyncPolicy.Strict },
        new() { Path = "config", Policy = SyncPolicy.AddOrUpdate },
        new() { Path = "options.txt", Policy = SyncPolicy.CreateIfMissing },
    ];
}

/// <summary>A file inside a build, stored by SHA-1 in the file storage.</summary>
public class BuildFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameBuildId { get; set; }

    /// <summary>Path relative to the instance root, forward slashes, e.g. "mods/jei.jar".</summary>
    public required string Path { get; set; }

    public required string Sha1 { get; set; }

    public long Size { get; set; }

    public FileSide Side { get; set; } = FileSide.Both;

    public FileInstallMode Mode { get; set; } = FileInstallMode.Required;

    /// <summary>Friendly name for optional mods in launcher settings.</summary>
    public string? DisplayName { get; set; }
}

/// <summary>Sync policy for a path inside the instance folder.</summary>
public class SyncRule
{
    public required string Path { get; set; }

    public SyncPolicy Policy { get; set; }
}
