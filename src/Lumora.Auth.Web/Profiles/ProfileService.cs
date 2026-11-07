using Lumora.Auth.Core.Entities;
using Lumora.Auth.Core.Profiles;
using Lumora.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Web.Profiles;

/// <summary>Creates and renames in-game profiles. Returns a Russian error message or null on success.</summary>
public sealed class ProfileService(LumoraDbContext db)
{
    public const string NameRules = "От 3 до 16 символов: латинские буквы, цифры и «_».";

    public Task<PlayerProfile?> FindByUserAsync(string userId, CancellationToken ct = default) =>
        db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task<string?> ValidateNameAsync(string? name, Guid? exceptProfile = null, CancellationToken ct = default)
    {
        if (!PlayerName.IsValid(name))
            return "Недопустимый ник. " + NameRules;
        // The column collation makes this comparison case-insensitive.
        if (await db.Profiles.AnyAsync(p => p.Name == name && p.Id != exceptProfile, ct))
            return "Этот ник уже занят.";
        return null;
    }

    public async Task<string?> CreateAsync(string userId, string name, CancellationToken ct = default)
    {
        if (await db.Profiles.AnyAsync(p => p.UserId == userId, ct))
            return "У аккаунта уже есть игровой профиль.";
        if (await ValidateNameAsync(name, null, ct) is { } error)
            return error;

        db.Profiles.Add(new PlayerProfile { UserId = userId, Name = name });
        return await SaveAsync(ct);
    }

    public async Task<string?> RenameAsync(string userId, string name, CancellationToken ct = default)
    {
        var profile = await FindByUserAsync(userId, ct);
        if (profile is null)
            return "Игровой профиль не найден.";
        if (profile.Name == name)
            return null;
        if (await ValidateNameAsync(name, profile.Id, ct) is { } error)
            return error;

        profile.Name = name;
        return await SaveAsync(ct);
    }

    private async Task<string?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            // Lost a race for the same name.
            db.ChangeTracker.Clear();
            return "Этот ник уже занят.";
        }
    }
}
