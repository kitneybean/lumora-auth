using Lumora.Contracts;

namespace Lumora.Auth.Core.Builds;

/// <summary>Rules for file paths inside a build (relative to the game folder).</summary>
public static class BuildPath
{
    /// <summary>Folders/files the launcher manages itself and builds must not ship.</summary>
    private static readonly string[] Reserved = ["saves", "screenshots", "logs", "crash-reports", "servers.dat", "launcher_profiles.json"];

    /// <summary>
    /// Normalizes to forward slashes without leading "./" or "/". Returns null when the path is unsafe
    /// (absolute, contains "..", empty segments or invalid characters) or reserved.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        p = p.TrimStart('/');
        if (p.Length == 0 || p.Length > 400 || p.Contains(':'))
            return null;

        var segments = p.Split('/');
        foreach (var s in segments)
        {
            if (s.Length == 0 || s == "." || s == ".." || s.Trim() != s)
                return null;
            if (s.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || s.Any(char.IsControl))
                return null;
        }
        if (Reserved.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            return null;
        return string.Join('/', segments);
    }

    /// <summary>Joins a folder and a file name and normalizes the result.</summary>
    public static string? Combine(string? folder, string fileName)
    {
        var name = System.IO.Path.GetFileName(fileName.Replace('\\', '/'));
        return Normalize(string.IsNullOrWhiteSpace(folder) ? name : folder.Trim().TrimEnd('/', '\\') + "/" + name);
    }

    /// <summary>Default install mode and side for a newly added file.</summary>
    public static (FileSide Side, FileInstallMode Mode) Defaults(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith("shaderpacks/") || lower.StartsWith("resourcepacks/"))
            return (FileSide.Client, FileInstallMode.OptionalOn);
        return (FileSide.Both, FileInstallMode.Required);
    }
}
