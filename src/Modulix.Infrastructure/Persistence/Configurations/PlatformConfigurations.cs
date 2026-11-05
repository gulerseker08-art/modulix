using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
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

internal sealed class PlatformAuditEntryConfiguration : IEntityTypeConfiguration<PlatformAuditEntry>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEntry> builder)
    {
        // Platform geneli, RLS dışı, ekleme-yalnız (migration uygulama rolüne UPDATE/DELETE'i yasaklar). Firma silinse de saklanır:
        // yabancı anahtar yoktur ve sütun adı TenantId olmadığı için tenant dışa aktarma/silme kapsamında değildir.
        builder.ToTable("PlatformAuditLog", DatabaseSchemas.Platform);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.ActorUserId).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(PlatformAuditEntry.ActionMaxLength).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(PlatformAuditEntry.ReasonMaxLength);
        builder.Property(e => e.Details).HasMaxLength(PlatformAuditEntry.DetailsMaxLength);
        builder.Property(e => e.TraceId).HasMaxLength(64);
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.TargetTenantId, e.OccurredAt });
    }
}

internal sealed class MembershipConfiguration : IEntityTypeConfiguration<Membership>
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

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        // Plan tanımı platform geneli ve RLS dışıdır; TenantId yoktur. Silinmez (IsActive ile kapatılır).
        builder.ToTable("Plans", DatabaseSchemas.Platform);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Key).HasMaxLength(Plan.KeyMaxLength).IsRequired();
        builder.HasIndex(p => p.Key).IsUnique();
        builder.Property(p => p.Name).HasMaxLength(Plan.NameMaxLength).IsRequired();
        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();
        builder.HasMany(p => p.Modules).WithOne().HasForeignKey(m => m.PlanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Limits).WithOne().HasForeignKey(l => l.PlanId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Modules).AutoInclude();
        builder.Navigation(p => p.Limits).AutoInclude();
    }
}

internal sealed class PlanModuleConfiguration : IEntityTypeConfiguration<PlanModule>
{
    public void Configure(EntityTypeBuilder<PlanModule> builder)
    {
        builder.ToTable("PlanModules", DatabaseSchemas.Platform);
        builder.HasKey(m => new { m.PlanId, m.ModuleKey });
        builder.Property(m => m.ModuleKey).HasMaxLength(64).IsRequired();
    }
}

internal sealed class PlanLimitConfiguration : IEntityTypeConfiguration<PlanLimit>
{
    public void Configure(EntityTypeBuilder<PlanLimit> builder)
    {
        builder.ToTable("PlanLimits", DatabaseSchemas.Platform);
        builder.HasKey(l => new { l.PlanId, l.LimitKey });
        builder.Property(l => l.LimitKey).HasMaxLength(64).IsRequired();
        builder.Property(l => l.Value).IsRequired();
    }
}

internal sealed class TenantSubscriptionConfiguration : IEntityTypeConfiguration<TenantSubscription>
{
    public void Configure(EntityTypeBuilder<TenantSubscription> builder)
    {
        // Abonelik kaydı platform tablosudur (modül hakkı gibi): TenantId taşır, RLS dışıdır, firmayı yalnızca platform yöneticisi değiştirir.
        builder.ToTable("TenantSubscriptions", DatabaseSchemas.Platform);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.StartsAt).IsRequired();
        builder.Property(s => s.Reason).HasMaxLength(PlatformAuditEntry.ReasonMaxLength);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);

        // Bir firmanın en çok bir geçerli (kapanmamış) aboneliği olur.
        builder.HasIndex(s => s.TenantId).IsUnique().HasFilter("[EndedAt] IS NULL");
        builder.HasIndex(s => new { s.TenantId, s.StartsAt });
    }
}

internal sealed class LimitOverrideConfiguration : IEntityTypeConfiguration<LimitOverride>
{
    public void Configure(EntityTypeBuilder<LimitOverride> builder)
    {
        builder.ToTable("LimitOverrides", DatabaseSchemas.Platform);
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.LimitKey).HasMaxLength(64).IsRequired();
        builder.Property(o => o.Reason).HasMaxLength(LimitOverride.ReasonMaxLength).IsRequired();
        builder.Property(o => o.ValidUntil).IsRequired();
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.HasIndex(o => new { o.TenantId, o.LimitKey, o.CreatedAt });
    }
}

internal sealed class SupportAccessGrantConfiguration : IEntityTypeConfiguration<SupportAccessGrant>
{
    public void Configure(EntityTypeBuilder<SupportAccessGrant> builder)
    {
        // Platform tablosu (RLS dışı): destek erişimini platform yönetir, firma yazamaz. TenantId taşır; firma silinince birlikte silinir.
        // Üyelik yabancı anahtarıyla bağlıdır. Süresi dolan ya da geri alınan kayıt silinmez.
        builder.ToTable("SupportAccessGrants", DatabaseSchemas.Platform);
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.Reason).HasMaxLength(SupportAccessLimits.ReasonMaxLength).IsRequired();
        builder.Property(g => g.GrantedAt).IsRequired();
        builder.Property(g => g.ExpiresAt).IsRequired();
        builder.Property(g => g.EndReason).HasMaxLength(SupportAccessLimits.ReasonMaxLength);
        builder.HasIndex(g => new { g.MembershipId, g.ExpiresAt });
        builder.HasIndex(g => new { g.TenantId, g.GrantedAt });
    }
}
