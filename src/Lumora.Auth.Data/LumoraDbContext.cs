using Lumora.Auth.Core.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lumora.Auth.Data;

public class LumoraDbContext(DbContextOptions<LumoraDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<PlayerProfile> Profiles => Set<PlayerProfile>();
    public DbSet<Texture> Textures => Set<Texture>();
    public DbSet<YggdrasilToken> Tokens => Set<YggdrasilToken>();
    public DbSet<GameBuild> Builds => Set<GameBuild>();
    public DbSet<BuildFile> BuildFiles => Set<BuildFile>();
    public DbSet<GameServer> Servers => Set<GameServer>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasCollation("und-ci", locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        builder.Entity<PlayerProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(16);
            // Names are case-insensitively unique, like Mojang's.
            e.HasIndex(p => p.Name).IsUnique().UseCollation("und-ci");
            e.HasIndex(p => p.UserId).IsUnique();
            e.Property(p => p.SkinModel).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.SkinHash).HasMaxLength(64);
            e.Property(p => p.CapeHash).HasMaxLength(64);
            e.HasOne<ApplicationUser>()
                .WithOne(u => u.Profile)
                .HasForeignKey<PlayerProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Texture>(e =>
        {
            e.HasKey(t => t.Hash);
            e.Property(t => t.Hash).HasMaxLength(64);
            e.Property(t => t.Type).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<YggdrasilToken>(e =>
        {
            e.HasKey(t => t.AccessToken);
            e.Property(t => t.AccessToken).HasMaxLength(128);
            e.Property(t => t.ClientToken).HasMaxLength(128);
            e.HasIndex(t => t.UserId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PlayerProfile>().WithMany().HasForeignKey(t => t.ProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<GameBuild>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Name).HasMaxLength(128);
            e.Property(b => b.MinecraftVersion).HasMaxLength(32);
            e.Property(b => b.LoaderVersion).HasMaxLength(64);
            e.Property(b => b.Loader).HasConversion<string>().HasMaxLength(16);
            e.HasMany(b => b.Files).WithOne().HasForeignKey(f => f.GameBuildId).OnDelete(DeleteBehavior.Cascade);
            e.OwnsMany(b => b.SyncRules, r =>
            {
                r.ToJson();
                r.Property(x => x.Policy).HasConversion<string>();
            });
        });

        builder.Entity<BuildFile>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Path).HasMaxLength(512);
            e.Property(f => f.Sha1).HasMaxLength(40);
            e.Property(f => f.Side).HasConversion<string>().HasMaxLength(16);
            e.Property(f => f.Mode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(f => new { f.GameBuildId, f.Path }).IsUnique();
        });

        builder.Entity<GameServer>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).HasMaxLength(128);
            e.Property(s => s.Address).HasMaxLength(255);
            e.Ignore(s => s.ConnectionString);
            e.HasOne(s => s.GameBuild).WithMany(b => b.Servers).HasForeignKey(s => s.GameBuildId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

}
