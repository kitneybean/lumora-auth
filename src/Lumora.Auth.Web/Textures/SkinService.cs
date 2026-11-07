using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Textures;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Textures;

/// <summary>Stores textures by hash and assigns skins to profiles.</summary>
public sealed class SkinService(LumoraDbContext db, LumoraPaths paths, ILogger<SkinService> logger)
{
    /// <summary>Validates and stores the PNG, then makes it the profile's skin. Returns an error message or null.</summary>
    public async Task<string?> SetSkinAsync(Guid profileId, byte[] png, SkinModel model, CancellationToken ct = default)
    {
        TextureImage image;
        try
        {
            image = TextureImages.Process(png, TextureType.Skin);
        }
        catch (TextureImages.InvalidTextureException e)
        {
            return e.Message;
        }

        if (!await db.Profiles.AnyAsync(p => p.Id == profileId, ct))
            return "Игровой профиль не найден.";

        await StoreAsync(image, ct);
        await db.Profiles.Where(p => p.Id == profileId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.SkinHash, image.Hash)
            .SetProperty(p => p.SkinModel, model), ct);
        logger.LogInformation("Skin of profile {Profile} set to {Hash} ({Model})", profileId, image.Hash, model);
        return null;
    }

    public async Task SetModelAsync(Guid profileId, SkinModel model, CancellationToken ct = default) =>
        await db.Profiles.Where(p => p.Id == profileId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SkinModel, model), ct);

    /// <summary>Back to the default Steve/Alex skin.</summary>
    public async Task ResetSkinAsync(Guid profileId, CancellationToken ct = default) =>
        await db.Profiles.Where(p => p.Id == profileId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SkinHash, (string?)null), ct);

    private async Task StoreAsync(TextureImage image, CancellationToken ct)
    {
        var file = paths.TextureFile(image.Hash);
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(paths.Textures);
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, image.Png, ct);
            try
            {
                File.Move(tmp, file);
            }
            catch (IOException) when (File.Exists(file))
            {
                // Same texture uploaded concurrently: identical content, keep the existing file.
                File.Delete(tmp);
            }
        }

        if (!await db.Textures.AnyAsync(t => t.Hash == image.Hash, ct))
        {
            db.Textures.Add(new Texture { Hash = image.Hash, Type = image.Type, Width = image.Width, Height = image.Height });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
