using Lumora.Contracts;

namespace Lumora.Shared.Sync;

/// <summary>What the launcher must do to bring an instance folder in line with a build manifest.</summary>
public sealed record SyncPlan(IReadOnlyList<BuildFileDto> Downloads, IReadOnlyList<string> Deletions)
{
    public bool IsEmpty => Downloads.Count == 0 && Deletions.Count == 0;
}

/// <summary>
/// Decides downloads and deletions from the manifest, the folder sync rules and the player's optional-mod choices.
/// Pure logic: the caller supplies local hashes and the files present in strict folders.
/// </summary>
public static class FileSyncPlanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static bool IsEnabled(BuildFileDto file, IReadOnlyDictionary<string, bool> optionalChoices) =>
        file.Mode == FileInstallMode.Required
        || (optionalChoices.TryGetValue(file.Path, out var on) ? on : file.Mode == FileInstallMode.OptionalOn);

    /// <summary>Policy of the most specific rule covering the path; files outside any rule are added/updated.</summary>
    public static SyncPolicy PolicyFor(string path, IReadOnlyList<SyncRuleDto> rules)
    {
        SyncRuleDto? best = null;
        foreach (var rule in rules)
        {
            var p = rule.Path.TrimEnd('/');
            var matches = PathComparer.Equals(path, p) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase);
            if (matches && (best is null || p.Length > best.Path.TrimEnd('/').Length))
                best = rule;
        }
        return best?.Policy ?? SyncPolicy.AddOrUpdate;
    }

    /// <param name="localSha1">SHA-1 of the local file at a build path, or null if missing.</param>
    /// <param name="strictFolderContents">Relative paths of files currently inside each strict folder.</param>
    public static SyncPlan Plan(
        IReadOnlyList<BuildFileDto> files,
        IReadOnlyList<SyncRuleDto> rules,
        IReadOnlyDictionary<string, bool> optionalChoices,
        Func<string, string?> localSha1,
        IEnumerable<string> strictFolderContents)
    {
        var downloads = new List<BuildFileDto>();
        var deletions = new HashSet<string>(PathComparer);
        var wanted = new HashSet<string>(PathComparer);

        foreach (var file in files)
        {
            var policy = PolicyFor(file.Path, rules);
            var local = localSha1(file.Path);
            if (IsEnabled(file, optionalChoices))
            {
                wanted.Add(file.Path);
                var needed = policy == SyncPolicy.CreateIfMissing
                    ? local is null
                    : !string.Equals(local, file.Sha1, StringComparison.OrdinalIgnoreCase);
                if (needed)
                    downloads.Add(file);
            }
            else if (local is not null && (policy == SyncPolicy.Strict || string.Equals(local, file.Sha1, StringComparison.OrdinalIgnoreCase)))
            {
                // A disabled optional file: remove our copy, never a file the player changed in a non-strict folder.
                deletions.Add(file.Path);
            }
        }

        foreach (var path in strictFolderContents)
        {
            if (!wanted.Contains(path) && PolicyFor(path, rules) == SyncPolicy.Strict)
                deletions.Add(path);
        }

        return new SyncPlan(downloads, deletions.Order(PathComparer).ToList());
    }
}
