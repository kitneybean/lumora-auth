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
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<IpRule> IpRules => Set<IpRule>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasCollation("und-ci", locale: "und-u-ks-level2", provider: "icu", deterministic: false);

        builder.Entity<PlayerProfile>(e =>
        {
            e.HasKey(p => p.Id);
            // Case-insensitive collation: names compare and stay unique like Mojang's.
            e.Property(p => p.Name).HasMaxLength(16).UseCollation("und-ci");
            e.HasIndex(p => p.Name).IsUnique();
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
            e.Ignore(b => b.HasUnpublishedChanges);
            e.Property(b => b.PublishedManifestJson).HasColumnType("jsonb");
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

        builder.Entity<IpRule>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Network).HasMaxLength(64);
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Source).HasMaxLength(16);
            e.Property(r => r.Reason).HasMaxLength(256);
            e.Property(r => r.CreatedBy).HasMaxLength(256);
            e.HasIndex(r => r.Network);
            e.HasIndex(r => r.ExpiresAt);
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.UserId).HasMaxLength(64);
            e.Property(a => a.UserName).HasMaxLength(256);
            e.Property(a => a.Ip).HasMaxLength(64);
            e.Property(a => a.Details).HasMaxLength(2000);
            e.HasIndex(a => a.At);
            e.HasIndex(a => a.Action);
            e.HasIndex(a => a.Ip);
        });

        builder.Entity<SiteSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.ServerName).HasMaxLength(64);
            e.Property(s => s.SmtpHost).HasMaxLength(255);
            e.Property(s => s.SmtpUsername).HasMaxLength(255);
            e.Property(s => s.MailFromAddress).HasMaxLength(255);
            e.Property(s => s.MailFromName).HasMaxLength(128);
            e.Property(s => s.SmtpSecurity).HasConversion<string>().HasMaxLength(16);
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
