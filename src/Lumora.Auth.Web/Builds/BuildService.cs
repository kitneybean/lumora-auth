using System.IO.Compression;
using System.Text.Json;
using Lumora.Auth.Core.Builds;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using Lumora.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Lumora.Auth.Web.Builds;

/// <summary>Admin operations on builds and their files. Uses short-lived contexts (safe for Blazor Server).</summary>
public sealed class BuildService(
    IDbContextFactory<LumoraDbContext> dbFactory, BuildFileStore store, TimeProvider time,
    IHttpClientFactory httpFactory, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, Agents.AgentService agents)
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
        var result = await ImportArchiveAsync(buildId, zip, folder, null, ct);
        return new AddResult(result.Added, result.Updated, result.Skipped);
    }

    public sealed record ImportResult(int Added, int Updated, IReadOnlyList<string> Skipped, IReadOnlyList<string> Notes);

    /// <summary>
    /// Imports an archive: a Modrinth/Prism <c>.mrpack</c> (versions are taken from it, mods downloaded from Modrinth),
    /// a CurseForge export (versions and overrides), or a plain ZIP of the game folder
    /// (wrapping folders like <c>.minecraft/</c> or <c>overrides/</c> are stripped).
    /// </summary>
    public async Task<ImportResult> ImportArchiveAsync(Guid buildId, Stream zip, string? folder, IProgress<string>? progress, CancellationToken ct = default)
    {
        await using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var notes = new List<string>();
        var files = new List<(ZipArchiveEntry Entry, string Path, FileSide? Side)>();
        var skipped = new List<string>();
        int added = 0, updated = 0;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        if (MrpackIndex.Read(archive) is { } mrpack && folder is null)
        {
            notes.Add(await ApplyVersionsAsync(db, buildId, mrpack.Name, mrpack.MinecraftVersion, mrpack.Loader, ct));
            var downloaded = await DownloadMrpackFilesAsync(db, buildId, mrpack, progress, skipped, ct);
            added += downloaded.Added;
            updated += downloaded.Updated;
            AddOverrides(entries, "overrides/", null, files);
            AddOverrides(entries, "client-overrides/", FileSide.Client, files);
            AddOverrides(entries, "server-overrides/", FileSide.Server, files);
        }
        else if (CurseForgeManifest.Read(archive) is { } curse && folder is null)
        {
            notes.Add(await ApplyVersionsAsync(db, buildId, curse.Name, curse.Minecraft.Version, curse.Loader, ct));
            AddOverrides(entries, (curse.Overrides ?? "overrides").TrimEnd('/') + "/", null, files);
            if (curse.Files is { Count: > 0 } mods)
                notes.Add($"В экспорте CurseForge моды записаны только номерами ({mods.Count} шт.) — скачать их без ключа CurseForge нельзя. " +
                          "Экспортируйте сборку из CurseForge как «.zip с модами» или из Prism/Modrinth как .mrpack, либо загрузите папку mods отдельно.");
        }
        else
        {
            var prefix = folder is null ? ZipLayout.DetectPrefix(entries.Select(e => e.FullName).ToList()) : "";
            if (prefix.Length > 0)
                notes.Add($"Папка «{prefix.TrimEnd('/')}» в архиве принята за папку игры.");
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var relative = entry.FullName[prefix.Length..];
                var raw = string.IsNullOrWhiteSpace(folder) ? relative : folder.Trim().TrimEnd('/') + "/" + relative;
                files.Add((entry, raw, null));
            }
        }

        var i = 0;
        foreach (var (entry, raw, side) in files)
        {
            var path = BuildPath.Normalize(raw);
            if (path is null)
            {
                skipped.Add(entry.FullName);
                continue;
            }
            if (++i % 25 == 0)
                progress?.Report($"Распаковываю файлы: {i} из {files.Count}…");
            await using var stream = await entry.OpenAsync(ct);
            var (sha1, size) = await store.SaveAsync(stream, ct);
            if (await UpsertAsync(db, buildId, path, sha1, size, ct, side))
                added++;
            else
                updated++;
        }
        await TouchAsync(db, buildId, ct);
        return new ImportResult(added, updated, skipped, notes);
    }

    private static void AddOverrides(List<ZipArchiveEntry> entries, string prefix, FileSide? side, List<(ZipArchiveEntry, string, FileSide?)> target)
    {
        foreach (var e in entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            target.Add((e, e.FullName[prefix.Length..], side));
    }

    private static async Task<string> ApplyVersionsAsync(LumoraDbContext db, Guid buildId, string? packName,
        string? minecraft, (LoaderType Loader, string? Version) loader, CancellationToken ct)
    {
        var build = await db.Builds.FirstAsync(b => b.Id == buildId, ct);
        if (minecraft is not null)
            build.MinecraftVersion = minecraft;
        build.Loader = loader.Loader;
        build.LoaderVersion = loader.Version;
        await db.SaveChangesAsync(ct);
        return $"Сборка «{packName}»: Minecraft {build.MinecraftVersion}, {build.Loader} {build.LoaderVersion} — версии взяты из архива."
               + (loader.Loader is LoaderType.Forge or LoaderType.Fabric ? " Внимание: лаунчер пока умеет ставить только NeoForge и Vanilla." : "");
    }

    private async Task<(int Added, int Updated)> DownloadMrpackFilesAsync(LumoraDbContext db, Guid buildId, MrpackIndex index,
        IProgress<string>? progress, List<string> skipped, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(BuildService));
        using var gate = new SemaphoreSlim(6);
        var done = 0;
        var results = await Task.WhenAll(index.Files.Select(async file =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (file.SafeDownloadUrl is not { } url)
                    return (file, (string?)null, 0L, $"{file.Path} (нет разрешённой ссылки на скачивание)");
                await using var stream = await http.GetStreamAsync(url, ct);
                var (sha1, size) = await store.SaveAsync(stream, ct);
                if (file.Hashes.TryGetValue("sha1", out var expected) && !expected.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    return (file, null, 0L, $"{file.Path} (контрольная сумма не совпала)");
                progress?.Report($"Скачиваю моды с Modrinth: {Interlocked.Increment(ref done)} из {index.Files.Count}…");
                return (file, sha1, size, (string?)null);
            }
            catch (HttpRequestException e)
            {
                return (file, null, 0L, $"{file.Path} ({e.Message})");
            }
            finally
            {
                gate.Release();
            }
        }));

        int added = 0, updated = 0;
        foreach (var (file, sha1, size, error) in results)
        {
            var path = BuildPath.Normalize(file.Path);
            if (error is not null || sha1 is null || path is null)
            {
                skipped.Add(error ?? file.Path);
                continue;
            }
            var mode = file.OptionalOnClient ? FileInstallMode.OptionalOn : (FileInstallMode?)null;
            if (await UpsertAsync(db, buildId, path, sha1, size, ct, file.Side, mode))
                added++;
            else
                updated++;
        }
        return (added, updated);
    }

    public sealed record ModReport(IReadOnlyList<ModProblem> Problems, IReadOnlyDictionary<string, ModInfo> ModsBySha1, int JarCount);

    /// <summary>Reads mod metadata of every jar in mods/ and reports duplicates and missing dependencies.</summary>
    public async Task<ModReport> AnalyzeModsAsync(Guid buildId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jars = await db.BuildFiles.AsNoTracking()
            .Where(f => f.GameBuildId == buildId && f.Path.StartsWith("mods/") && f.Path.EndsWith(".jar") && f.Side != FileSide.Server)
            .Select(f => new { f.Path, f.Sha1 })
            .ToListAsync(ct);

        var infos = new List<(string, JarInfo)>();
        var bySha1 = new Dictionary<string, ModInfo>();
        foreach (var jar in jars)
        {
            var info = await Task.Run(() => cache.GetOrCreate<JarInfo>("modinfo:" + jar.Sha1, entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromHours(6);
                try
                {
                    using var stream = File.OpenRead(store.PathOf(jar.Sha1));
                    return ModMetadata.Read(stream);
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    return JarInfo.Empty;
                }
            })!, ct);
            infos.Add((jar.Path, info));
            if (info.Mods.Count > 0)
                bySha1[jar.Sha1] = info.Mods[0];
        }
        return new ModReport(ModCheck.Analyze(infos), bySha1, jars.Count);
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
        build.PublishedServerManifestJson = JsonSerializer.Serialize(ServerManifestOf(build), LumoraJson.Options);
        build.IsPublished = true;
        build.PublishedAt = time.GetUtcNow();
        build.UpdatedAt = build.PublishedAt.Value;
        await db.SaveChangesAsync(ct);
        _ = agents.PushBuildAsync(build.Id);
        return build.Revision;
    }

    /// <summary>
    /// What a game server needs from the build: files for both sides and server-only files (optional mods that are
    /// off by default stay out). URLs, server id and authlib-injector are filled in per request.
    /// </summary>
    public static ServerManifestDto ServerManifestOf(GameBuild build) => new(
        Guid.Empty, build.Id, build.Name, build.Revision, build.MinecraftVersion, build.Loader, build.LoaderVersion,
        build.JavaMajorVersion, build.JvmArguments,
        build.Files.Where(f => f.Side != FileSide.Client && f.Mode != FileInstallMode.OptionalOff).OrderBy(f => f.Path)
            .Select(f => new BuildFileDto(f.Path, f.Sha1, f.Size, "", FileInstallMode.Required, f.DisplayName)).ToList(),
        build.SyncRules.Select(r => new SyncRuleDto(r.Path, r.Policy)).ToList(),
        null, "");

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

    private static async Task<bool> UpsertAsync(LumoraDbContext db, Guid buildId, string path, string sha1, long size, CancellationToken ct,
        FileSide? side = null, FileInstallMode? mode = null)
    {
        var existing = await db.BuildFiles.FirstOrDefaultAsync(f => f.GameBuildId == buildId && f.Path == path, ct);
        if (existing is not null)
        {
            existing.Sha1 = sha1;
            existing.Size = size;
            await db.SaveChangesAsync(ct);
            return false;
        }
        var (defaultSide, defaultMode) = BuildPath.Defaults(path);
        db.BuildFiles.Add(new BuildFile
        {
            GameBuildId = buildId, Path = path, Sha1 = sha1, Size = size,
            Side = side ?? defaultSide, Mode = mode ?? defaultMode,
        });
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
