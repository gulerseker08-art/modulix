using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>, IPlatformConfiguration
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants", DatabaseSchemas.Platform);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(Tenant.SlugMaxLength).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
        builder.Property(t => t.Status).IsRequired();
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.ProfileKey).HasMaxLength(Modulix.Core.Customization.SectorProfileCatalog.KeyMaxLength);
        builder.Ignore(t => t.IsActive);
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>, IPlatformConfiguration
{
    public void Configure(EntityTypeBuilder<Membership> builder)
    {
        builder.ToTable("Memberships", DatabaseSchemas.Platform);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Status).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();
        builder.Ignore(m => m.IsActive);

        // Bir kullanıcı bir tenant'a yalnızca bir kez üye olur.
        builder.HasIndex(m => new { m.TenantId, m.UserId }).IsUnique();
        builder.HasIndex(m => m.UserId);

        // Saatlik davet sınırı tenant'ın son davetlerini sayar.
        builder.HasIndex(m => new { m.TenantId, m.InvitedAt });

        // Kayıtlar silinmez; tenant kapatma ve üyelik devre dışı bırakma durum alanıyla yapılır.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ModulixUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantSettingConfiguration : IEntityTypeConfiguration<Modulix.Core.Settings.TenantSetting>
{
    public void Configure(EntityTypeBuilder<Modulix.Core.Settings.TenantSetting> builder)
    {
        // Tenant verisi: core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("TenantSettings", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Key).HasMaxLength(Modulix.Core.Settings.TenantSetting.KeyMaxLength).IsRequired();
        builder.Property(s => s.Value).HasMaxLength(Modulix.Core.Settings.TenantSetting.ValueMaxLength).IsRequired();
        builder.Property(s => s.CompanyId);
        builder.Property(s => s.UpdatedAt).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken();

        // Şirket başına ayar (ADR-0008, adım 5): tenant geneli ayarda (CompanyId boş) anahtar tektir; şirket başına
        // ayarda (CompanyId dolu) anahtar şirket başına tektir. İki filtreli benzersiz index bunu ayırır.
        builder.HasIndex(s => new { s.TenantId, s.Key }).IsUnique().HasFilter("[CompanyId] IS NULL");
        builder.HasIndex(s => new { s.TenantId, s.CompanyId, s.Key }).IsUnique().HasFilter("[CompanyId] IS NOT NULL");
    }
}

internal sealed class TenantLogoConfiguration : IEntityTypeConfiguration<Modulix.Core.Settings.TenantLogo>
{
    public void Configure(EntityTypeBuilder<Modulix.Core.Settings.TenantLogo> builder)
    {
        // Tenant verisi (Y6b): firma başına bir logo; Row-Level Security ile korunur.
        builder.ToTable("TenantLogos", DatabaseSchemas.Core);
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.TenantId).IsRequired();
        builder.Property(l => l.ContentType).HasMaxLength(50).IsUnicode(false).IsRequired();
        builder.Property(l => l.Data).IsRequired();
        builder.Property(l => l.UpdatedAt).IsRequired();
        builder.Property(l => l.Version).IsConcurrencyToken();
        builder.HasIndex(l => l.TenantId).IsUnique();
    }
}
