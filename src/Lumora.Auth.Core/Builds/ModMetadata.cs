using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumora.Auth.Core.Builds;

public sealed record ModDependency(string ModId, bool Required, string? VersionRange, string? Side);

/// <summary>A mod declared inside a jar (one jar can declare several).</summary>
public sealed record ModInfo(string ModId, string? Version, string? Name, IReadOnlyList<ModDependency> Dependencies);

/// <summary>What a jar declares: its own mods plus mods bundled inside it (jar-in-jar).</summary>
public sealed record JarInfo(IReadOnlyList<ModInfo> Mods, IReadOnlyList<string> BundledModIds)
{
    public static JarInfo Empty { get; } = new([], []);
}

/// <summary>
/// Reads mod ids, versions and dependencies from NeoForge/Forge (<c>META-INF/neoforge.mods.toml</c>,
/// <c>META-INF/mods.toml</c>) and Fabric (<c>fabric.mod.json</c>) jars.
/// </summary>
public static partial class ModMetadata
{
    public static JarInfo Read(Stream jar)
    {
        using var archive = new ZipArchive(jar, ZipArchiveMode.Read, leaveOpen: true);
        var mods = ReadMods(archive);

        var bundled = new List<string>();
        foreach (var entry in archive.Entries.Where(e =>
                     e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                     || e.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var nested = new MemoryStream();
                using (var s = entry.Open())
                    s.CopyTo(nested);
                nested.Position = 0;
                using var inner = new ZipArchive(nested, ZipArchiveMode.Read);
                bundled.AddRange(ReadMods(inner).Select(m => m.ModId));
            }
            catch (InvalidDataException)
            {
                // Not a readable jar: ignore.
            }
        }
        return new JarInfo(mods, bundled);
    }

    private static List<ModInfo> ReadMods(ZipArchive archive)
    {
        var toml = archive.GetEntry("META-INF/neoforge.mods.toml") ?? archive.GetEntry("META-INF/mods.toml");
        if (toml is not null)
        {
            using var reader = new StreamReader(toml.Open());
            return ParseModsToml(reader.ReadToEnd());
        }
        var fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using var stream = fabric.Open();
            return ParseFabric(stream);
        }
        return [];
    }

    /// <summary>Minimal TOML reader for the parts of mods.toml we need ([[mods]] and [[dependencies.x]]).</summary>
    public static List<ModInfo> ParseModsToml(string text)
    {
        var mods = new List<Dictionary<string, string>>();
        var deps = new List<(string Owner, Dictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                var section = line.Trim('[', ']', ' ');
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (section == "mods")
                    mods.Add(current);
                else if (section.StartsWith("dependencies.", StringComparison.Ordinal))
                    deps.Add((section["dependencies.".Length..].Trim('"'), current));
                else
                    current = null;
                continue;
            }
            if (line.StartsWith('['))
            {
                current = null;
                continue;
            }
            var eq = line.IndexOf('=');
            if (current is null || eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
                continue; // multi-line strings (descriptions) are not needed
            current[key] = Unquote(value);
        }

        return mods.Where(m => m.ContainsKey("modId")).Select(m =>
        {
            var id = m["modId"];
            var dependencies = deps.Where(d => d.Owner.Equals(id, StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Values.ContainsKey("modId"))
                .Select(d => new ModDependency(
                    d.Values["modId"],
                    // NeoForge: type="required"; Forge: mandatory=true
                    d.Values.TryGetValue("type", out var type) ? type.Equals("required", StringComparison.OrdinalIgnoreCase)
                        : d.Values.TryGetValue("mandatory", out var mandatory) && mandatory.Equals("true", StringComparison.OrdinalIgnoreCase),
                    d.Values.GetValueOrDefault("versionRange"),
                    d.Values.GetValueOrDefault("side")))
                .ToList();
            var version = m.GetValueOrDefault("version");
            return new ModInfo(id, version == "${file.jarVersion}" ? null : version, m.GetValueOrDefault("displayName"), dependencies);
        }).ToList();
    }

    private static List<ModInfo> ParseFabric(Stream json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var id))
                return [];
            var deps = new List<ModDependency>();
            if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
                deps.AddRange(depends.EnumerateObject().Select(p => new ModDependency(p.Name, true, p.Value.ToString(), null)));
            return [new ModInfo(id.GetString()!, root.TryGetProperty("version", out var v) ? v.GetString() : null,
                root.TryGetProperty("name", out var n) ? n.GetString() : null, deps)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (line[i] == '#' && !inString)
                return line[..i];
        }
        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            return value[1..^1];
        return value;
    }
}

public sealed record ModProblem(string Kind, string Message);

/// <summary>Finds duplicate mods and missing required dependencies in a set of jars.</summary>
public static class ModCheck
{
    /// <summary>Provided by the game or the loader, never shipped as a jar.</summary>
    private static readonly HashSet<string> Platform = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "neoforge", "forge", "fml", "javafml", "java", "fabricloader", "fabric-loader", "quilt_loader",
    };

    public static List<ModProblem> Analyze(IReadOnlyList<(string Path, JarInfo Info)> jars)
    {
        var problems = new List<ModProblem>();
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in jars)
            foreach (var mod in info.Mods)
                (owners.TryGetValue(mod.ModId, out var list) ? list : owners[mod.ModId] = []).Add(path);

        foreach (var (modId, paths) in owners.Where(o => o.Value.Distinct().Count() > 1).OrderBy(o => o.Key))
            problems.Add(new ModProblem("duplicate", $"Мод «{modId}» есть в нескольких файлах: {string.Join(", ", paths.Distinct().Select(Path.GetFileName))}. Оставьте один."));

        var available = new HashSet<string>(owners.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var (_, info) in jars)
            available.UnionWith(info.BundledModIds);

        foreach (var (path, info) in jars)
            foreach (var mod in info.Mods)
                foreach (var dep in mod.Dependencies.Where(d => d.Required && !Platform.Contains(d.ModId) && !available.Contains(d.ModId)))
                    problems.Add(new ModProblem("missing", $"«{mod.Name ?? mod.ModId}» ({Path.GetFileName(path)}) требует мод «{dep.ModId}»{(dep.VersionRange is { Length: > 0 } r ? $" {r}" : "")} — его нет в сборке."));

        return problems.DistinctBy(p => p.Message).ToList();
    }
}
