using System.IO.Compression;
using System.Text.Json;
using Lumora.Auth.Core.Builds;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Lumora.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Builds;

/// <summary>Admin operations on builds and their files. Uses short-lived contexts (safe for Blazor Server).</summary>
public sealed class BuildService(IDbContextFactory<LumoraDbContext> dbFactory, BuildFileStore store, TimeProvider time)
{
    public sealed record BuildSummary(
        Guid Id, string Name, string MinecraftVersion, LoaderType Loader, string? LoaderVersion,
        int Revision, bool IsPublished, bool HasUnpublishedChanges, int FileCount, long TotalSize, int ServerCount);

    public sealed record AddResult(int Added, int Updated, IReadOnlyList<string> Skipped);

    public async Task<List<BuildSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var builds = await db.Builds.AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new
            {
                b.Id, b.Name, b.MinecraftVersion, b.Loader, b.LoaderVersion, b.Revision, b.IsPublished, b.PublishedAt, b.UpdatedAt,
                Files = b.Files.Count, Size = b.Files.Sum(f => (long?)f.Size) ?? 0, Servers = b.Servers.Count,
            })
            .ToListAsync(ct);
        return builds.Select(b => new BuildSummary(b.Id, b.Name, b.MinecraftVersion, b.Loader, b.LoaderVersion, b.Revision,
            b.IsPublished, b.PublishedAt is null || b.UpdatedAt > b.PublishedAt, b.Files, b.Size, b.Servers)).ToList();
    }

    public async Task<GameBuild?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Builds.AsNoTracking()
            .Include(b => b.Files.OrderBy(f => f.Path))
            .Include(b => b.Servers.OrderBy(s => s.SortOrder))
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<Guid> CreateAsync(string name, string minecraftVersion, LoaderType loader, string? loaderVersion, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var build = new GameBuild
        {
            Name = name.Trim(),
            MinecraftVersion = minecraftVersion.Trim(),
            Loader = loader,
            LoaderVersion = loader == LoaderType.Vanilla ? null : loaderVersion?.Trim(),
            SyncRules = GameBuild.DefaultSyncRules(),
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow(),
        };
        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);
        return build.Id;
    }

    /// <summary>Copies settings (not files) from the edited object.</summary>
    public async Task UpdateSettingsAsync(GameBuild edited, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var build = await db.Builds.FirstAsync(b => b.Id == edited.Id, ct);
        build.Name = edited.Name.Trim();
        build.Description = string.IsNullOrWhiteSpace(edited.Description) ? null : edited.Description.Trim();
        build.MinecraftVersion = edited.MinecraftVersion.Trim();
        build.Loader = edited.Loader;
        build.LoaderVersion = edited.Loader == LoaderType.Vanilla || string.IsNullOrWhiteSpace(edited.LoaderVersion) ? null : edited.LoaderVersion.Trim();
        build.JavaMajorVersion = edited.JavaMajorVersion;
        build.RecommendedMemoryMb = edited.RecommendedMemoryMb;
        build.JvmArguments = edited.JvmArguments.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        build.SyncRules = edited.SyncRules
            .Select(r => new SyncRule { Path = BuildPath.Normalize(r.Path) ?? "", Policy = r.Policy })
            .Where(r => r.Path.Length > 0)
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        build.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stores a single file under <paramref name="path"/> (replacing an existing one).</summary>
    public async Task<AddResult> AddFileAsync(Guid buildId, string path, Stream content, CancellationToken ct = default)
    {
        var normalized = BuildPath.Normalize(path);
        if (normalized is null)
            return new AddResult(0, 0, [path]);
        var (sha1, size) = await store.SaveAsync(content, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var added = await UpsertAsync(db, buildId, normalized, sha1, size, ct);
        await TouchAsync(db, buildId, ct);
        return added ? new AddResult(1, 0, []) : new AddResult(0, 1, []);
    }

    /// <summary>Unpacks a zip into the build, keeping folder structure (optionally under <paramref name="folder"/>).</summary>
    public async Task<AddResult> AddZipAsync(Guid buildId, Stream zip, string? folder, CancellationToken ct = default)
    {
        int added = 0, updated = 0;
        var skipped = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using (var archive = new ZipArchive(zip, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0)
                    continue;
                var raw = string.IsNullOrWhiteSpace(folder) ? entry.FullName : folder.Trim().TrimEnd('/') + "/" + entry.FullName;
                var path = BuildPath.Normalize(raw);
                if (path is null)
                {
                    skipped.Add(entry.FullName);
                    continue;
                }
                await using var stream = await entry.OpenAsync(ct);
                var (sha1, size) = await store.SaveAsync(stream, ct);
                if (await UpsertAsync(db, buildId, path, sha1, size, ct))
                    added++;
                else
                    updated++;
            }
        }
        await TouchAsync(db, buildId, ct);
        return new AddResult(added, updated, skipped);
    }

    public async Task UpdateFileAsync(Guid fileId, FileSide side, FileInstallMode mode, string? displayName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.BuildFiles.FirstAsync(f => f.Id == fileId, ct);
        file.Side = side;
        file.Mode = mode;
        file.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await TouchAsync(db, file.GameBuildId, ct);
    }

    public async Task DeleteFilesAsync(Guid buildId, IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.BuildFiles.Where(f => f.GameBuildId == buildId && fileIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
        await TouchAsync(db, buildId, ct);
    }

    /// <summary>Freezes the current state for launchers and bumps the revision.</summary>
    public async Task<int> PublishAsync(Guid buildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var build = await db.Builds.Include(b => b.Files).FirstAsync(b => b.Id == buildId, ct);
        build.Revision++;
        var manifest = new BuildManifestDto(
            build.Id, build.Name, build.Revision, build.MinecraftVersion, build.Loader, build.LoaderVersion,
            build.JavaMajorVersion, build.RecommendedMemoryMb, build.JvmArguments,
            build.Files.Where(f => f.Side != FileSide.Server).OrderBy(f => f.Path)
                .Select(f => new BuildFileDto(f.Path, f.Sha1, f.Size, "", f.Mode, f.DisplayName)).ToList(),
            build.SyncRules.Select(r => new SyncRuleDto(r.Path, r.Policy)).ToList(),
            []);
        build.PublishedManifestJson = JsonSerializer.Serialize(manifest, LumoraJson.Options);
        build.IsPublished = true;
        build.PublishedAt = time.GetUtcNow();
        build.UpdatedAt = build.PublishedAt.Value;
        await db.SaveChangesAsync(ct);
        return build.Revision;
    }

    /// <summary>Hides the build (and its servers) from launchers.</summary>
    public async Task UnpublishAsync(Guid buildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Builds.Where(b => b.Id == buildId).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsPublished, false), ct);
    }

    public async Task DeleteAsync(Guid buildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var build = await db.Builds.Include(b => b.Servers).FirstOrDefaultAsync(b => b.Id == buildId, ct);
        if (build is null)
            return;
        foreach (var server in build.Servers)
            server.GameBuildId = null;
        db.Builds.Remove(build);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> UpsertAsync(LumoraDbContext db, Guid buildId, string path, string sha1, long size, CancellationToken ct)
    {
        var existing = await db.BuildFiles.FirstOrDefaultAsync(f => f.GameBuildId == buildId && f.Path == path, ct);
        if (existing is not null)
        {
            existing.Sha1 = sha1;
            existing.Size = size;
            await db.SaveChangesAsync(ct);
            return false;
        }
        var (side, mode) = BuildPath.Defaults(path);
        db.BuildFiles.Add(new BuildFile { GameBuildId = buildId, Path = path, Sha1 = sha1, Size = size, Side = side, Mode = mode });
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task TouchAsync(LumoraDbContext db, Guid buildId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        var now = time.GetUtcNow();
        await db.Builds.Where(b => b.Id == buildId).ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedAt, now), ct);
    }
}
