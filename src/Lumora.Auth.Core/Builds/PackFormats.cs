using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumora.Contracts;

namespace Lumora.Auth.Core.Builds;

/// <summary>Modrinth modpack index (<c>modrinth.index.json</c> inside a .mrpack).</summary>
public sealed record MrpackIndex(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("versionId")] string? VersionId,
    [property: JsonPropertyName("files")] List<MrpackFile> Files,
    [property: JsonPropertyName("dependencies")] Dictionary<string, string> Dependencies)
{
    public static MrpackIndex? Read(ZipArchive archive)
    {
        var entry = archive.GetEntry("modrinth.index.json");
        if (entry is null)
            return null;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<MrpackIndex>(stream);
    }

    public string? MinecraftVersion => Dependencies.GetValueOrDefault("minecraft");

    public (LoaderType Loader, string? Version) Loader =>
        Dependencies.TryGetValue("neoforge", out var neo) ? (LoaderType.NeoForge, neo)
        : Dependencies.TryGetValue("forge", out var forge) ? (LoaderType.Forge, forge)
        : Dependencies.TryGetValue("fabric-loader", out var fabric) ? (LoaderType.Fabric, fabric)
        : (LoaderType.Vanilla, null);
}

public sealed record MrpackFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hashes")] Dictionary<string, string> Hashes,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env,
    [property: JsonPropertyName("downloads")] List<string> Downloads,
    [property: JsonPropertyName("fileSize")] long FileSize)
{
    /// <summary>Where the file is needed, from env.client / env.server ("required" | "optional" | "unsupported").</summary>
    public FileSide Side =>
        Env?.GetValueOrDefault("client") == "unsupported" ? FileSide.Server
        : Env?.GetValueOrDefault("server") == "unsupported" ? FileSide.Client
        : FileSide.Both;

    public bool OptionalOnClient => Env?.GetValueOrDefault("client") == "optional";

    /// <summary>The .mrpack spec only allows these download hosts.</summary>
    public static readonly string[] AllowedHosts = ["cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com"];

    public string? SafeDownloadUrl => Downloads.FirstOrDefault(u =>
        Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && AllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>CurseForge export (<c>manifest.json</c> + <c>overrides/</c>). Mods are referenced by id only.</summary>
public sealed record CurseForgeManifest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("minecraft")] CurseForgeMinecraft Minecraft,
    [property: JsonPropertyName("files")] List<JsonElement>? Files,
    [property: JsonPropertyName("overrides")] string? Overrides)
{
    public static CurseForgeManifest? Read(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json");
        if (entry is null)
            return null;
        try
        {
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<CurseForgeManifest>(stream);
            return manifest?.Minecraft?.Version is null ? null : manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public (LoaderType Loader, string? Version) Loader
    {
        get
        {
            var id = Minecraft.ModLoaders?.FirstOrDefault(l => l.Primary)?.Id ?? Minecraft.ModLoaders?.FirstOrDefault()?.Id ?? "";
            var dash = id.IndexOf('-');
            var (name, version) = dash > 0 ? (id[..dash], id[(dash + 1)..]) : (id, null);
            return name switch
            {
                "neoforge" => (LoaderType.NeoForge, version),
                "forge" => (LoaderType.Forge, version),
                "fabric" => (LoaderType.Fabric, version),
                _ => (LoaderType.Vanilla, null),
            };
        }
    }
}

public sealed record CurseForgeMinecraft(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("modLoaders")] List<CurseForgeLoader>? ModLoaders);

public sealed record CurseForgeLoader([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("primary")] bool Primary);

/// <summary>Figures out which folder inside a ZIP is the game folder.</summary>
public static class ZipLayout
{
    private static readonly string[] GameFolders = ["mods", "config", "defaultconfigs", "kubejs", "resourcepacks", "shaderpacks", "scripts"];

    /// <summary>
    /// Prefix to strip: ".minecraft/" or "minecraft/" (Prism/MultiMC export), "overrides/" (modpack exports),
    /// or a single wrapping folder that contains game folders. Empty when the ZIP is already the game folder.
    /// </summary>
    public static string DetectPrefix(IReadOnlyList<string> entries)
    {
        foreach (var known in new[] { ".minecraft/", "minecraft/", "overrides/" })
        {
            if (entries.Any(e => e.StartsWith(known, StringComparison.OrdinalIgnoreCase)))
                return known;
        }
        if (entries.Any(e => GameFolders.Any(g => e.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase))))
            return "";

        var roots = entries.Select(e => e.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 1 && entries.Any(e => e.Contains('/')))
        {
            var root = roots[0] + "/";
            var inner = entries.Where(e => e.StartsWith(root, StringComparison.OrdinalIgnoreCase)).Select(e => e[root.Length..]).ToList();
            return root + DetectPrefix(inner);
        }
        return "";
    }
}
