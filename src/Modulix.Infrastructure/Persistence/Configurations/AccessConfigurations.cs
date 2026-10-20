using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ModuleEntitlementConfiguration : IEntityTypeConfiguration<ModuleEntitlement>
{
    public void Configure(EntityTypeBuilder<ModuleEntitlement> builder)
    {
        // Platform geneli: tenant kullanıcıları değil, platform yönetir. RLS dışındadır.
        builder.ToTable("ModuleEntitlements", DatabaseSchemas.Platform);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ModuleKey).HasMaxLength(AccessKeys.ModuleKeyMaxLength).IsRequired();
        builder.Property(e => e.IsEnabled).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.ModuleKey }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        // Tenant verisi: core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("Roles", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(Role.NameMaxLength).IsRequired();
        builder.PrimitiveCollection(r => r.Permissions).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();

        // Rol atamaları (TenantId, RoleId) ile bağlanır: başka tenant'ın rolü veritabanı düzeyinde de atanamaz.
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
    }
}

internal sealed class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> builder)
    {
        // Tenant verisi: core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("MembershipRoles", DatabaseSchemas.Core);
        builder.HasKey(mr => mr.Id);
        builder.Property(mr => mr.Id).ValueGeneratedNever();
        builder.Property(mr => mr.TenantId).IsRequired();
        builder.HasIndex(mr => new { mr.TenantId, mr.MembershipId, mr.RoleId }).IsUnique();

        // Bileşik yabancı anahtarlar: üyelik de rol de atamayla aynı tenant'ta olmak zorundadır.
        builder.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(mr => new { mr.TenantId, mr.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(mr => new { mr.TenantId, mr.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
