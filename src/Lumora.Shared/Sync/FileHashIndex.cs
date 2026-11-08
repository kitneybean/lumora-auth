using System.Security.Cryptography;
using System.Text.Json;

namespace Lumora.Shared.Sync;

/// <summary>
/// Remembers SHA-1 of instance files by size and modification time, so a launch does not
/// re-hash hundreds of mods every time.
/// </summary>
public sealed class FileHashIndex
{
    private sealed record Entry(long Size, long ModifiedTicks, string Sha1);

    private readonly string _root;
    private readonly string _indexPath;
    private readonly Dictionary<string, Entry> _entries;

    public FileHashIndex(string instanceRoot, string indexPath)
    {
        _root = instanceRoot;
        _indexPath = indexPath;
        try
        {
            _entries = File.Exists(indexPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(indexPath)) ?? []
                : [];
        }
        catch (JsonException)
        {
            _entries = [];
        }
    }

    /// <summary>SHA-1 of the file at a relative path, or null if it does not exist.</summary>
    public string? Sha1Of(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        var info = new FileInfo(full);
        if (!info.Exists)
            return null;
        if (_entries.TryGetValue(relativePath, out var e) && e.Size == info.Length && e.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
            return e.Sha1;

        string sha1;
        using (var stream = info.OpenRead())
            sha1 = Convert.ToHexStringLower(SHA1.HashData(stream));
        _entries[relativePath] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha1);
        return sha1;
    }

    public void Forget(string relativePath) => _entries.Remove(relativePath);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(_entries));
    }

    public static string FileSha1(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }
}
