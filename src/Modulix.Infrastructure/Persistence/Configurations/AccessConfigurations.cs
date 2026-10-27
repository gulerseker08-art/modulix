using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Authorization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Teams;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ModuleEntitlementConfiguration : IEntityTypeConfiguration<ModuleEntitlement>, IPlatformConfiguration
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
        builder.Property(r => r.IsSystem).IsRequired();
        builder.Property(r => r.Scope).IsRequired().HasDefaultValue(Core.Contracts.Authorization.RecordScope.All);

        // Her tenant'ta en fazla bir sistem rolü ("Yönetici").
        builder.HasIndex(r => r.TenantId)
            .IsUnique()
            .HasFilter("[IsSystem] = 1")
            .HasDatabaseName("IX_Roles_TenantId_SystemRole");

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

        // ADR-0009: üyelik platform DB'sindedir; MembershipId burada düz Guid'dir (cross-database FK yok). Üyeliğin
        // bu tenant'a ait ve etkin olduğu servis katmanında (tenant çözümleme + platform defteri) doğrulanır.
        builder.Property(mr => mr.MembershipId).IsRequired();

        // Rol yabancı anahtarı tenant içindedir: başka tenant'ın rolü veritabanı düzeyinde de atanamaz.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(mr => new { mr.TenantId, mr.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        // Tenant verisi (Y8): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("Teams", DatabaseSchemas.Core);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(Team.NameMaxLength).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(Team.DescriptionMaxLength);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
        builder.HasAlternateKey(t => new { t.TenantId, t.Id });
    }
}

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("TeamMembers", DatabaseSchemas.Core);
        builder.HasKey(tm => tm.Id);
        builder.Property(tm => tm.Id).ValueGeneratedNever();
        builder.Property(tm => tm.TenantId).IsRequired();
        builder.HasIndex(tm => new { tm.TenantId, tm.TeamId, tm.MembershipId }).IsUnique();

        // Kapsam hesabı kişinin ekiplerinden başlar.
        builder.HasIndex(tm => new { tm.TenantId, tm.MembershipId });

        // ADR-0009: üyelik platform DB'sindedir; MembershipId burada düz Guid'dir (cross-database FK yok).
        builder.Property(tm => tm.MembershipId).IsRequired();

        // Ekip yabancı anahtarı tenant içindedir. Ekip silinince ekip üyeliği de silinir.
        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(tm => new { tm.TenantId, tm.TeamId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
