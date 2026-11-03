using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Organization;
using Modulix.Core.Platform;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class BusinessUnitConfiguration : IEntityTypeConfiguration<BusinessUnit>
{
    public void Configure(EntityTypeBuilder<BusinessUnit> builder)
    {
        // Tenant verisi (ADR-0026): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("BusinessUnits", DatabaseSchemas.Core);
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.TenantId).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(BusinessUnit.NameMaxLength).IsRequired();
        builder.Property(u => u.Code).HasMaxLength(BusinessUnit.CodeMaxLength);
        builder.Property(u => u.Kind).HasMaxLength(20).IsRequired();
        builder.Property(u => u.Path).HasMaxLength(BusinessUnit.PathMaxLength).IsRequired();
        builder.Property(u => u.Depth).IsRequired();
        builder.Property(u => u.IsActive).IsRequired();
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Ignore(u => u.IsRoot);

        builder.HasAlternateKey(u => new { u.TenantId, u.Id });

        // Aynı üst departmanda aynı ad kullanılamaz (kök için üst departman olmadığından filtre kökü dışarıda bırakır).
        builder.HasIndex(u => new { u.TenantId, u.ParentId, u.Name }).IsUnique();

        // Tenant başına tek kök departman.
        builder.HasIndex(u => u.TenantId)
            .IsUnique()
            .HasFilter("[ParentId] IS NULL")
            .HasDatabaseName("IX_BusinessUnits_TenantId_Root");

        // "Departman ve altı" sorguları kimlik yolunun önekiyle yapılır.
        builder.HasIndex(u => new { u.TenantId, u.Path });

        // Üst departman yabancı anahtarı tenant içindedir: başka tenant'ın departmanı veritabanı düzeyinde de üst olamaz.
        builder.HasOne<BusinessUnit>()
            .WithMany()
            .HasForeignKey(u => new { u.TenantId, u.ParentId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessUnitRoleConfiguration : IEntityTypeConfiguration<BusinessUnitRole>
{
    public void Configure(EntityTypeBuilder<BusinessUnitRole> builder)
    {
        // Tenant verisi (ADR-0026 K6): core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("BusinessUnitRoles", DatabaseSchemas.Core);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.InheritToChildren).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.BusinessUnitId, r.RoleId }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.RoleId });

        // Bileşik yabancı anahtarlar tenant içindedir: başka tenant'ın departmanı ya da rolü veritabanı düzeyinde de atanamaz.
        // Departman silinmez; rol silinirken atamaları aynı işlemde hedef role taşınır ya da kaldırılır.
        builder.HasOne<BusinessUnit>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.BusinessUnitId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Core.Authorization.Role>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BusinessUnitMemberConfiguration : IEntityTypeConfiguration<BusinessUnitMember>
{
    public void Configure(EntityTypeBuilder<BusinessUnitMember> builder)
    {
        builder.ToTable("BusinessUnitMembers", DatabaseSchemas.Core);
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.TenantId).IsRequired();

        // Her üyelik tam bir departmandadır (ADR-0026 K4).
        builder.HasIndex(m => new { m.TenantId, m.MembershipId }).IsUnique();
        builder.HasIndex(m => new { m.TenantId, m.BusinessUnitId });

        // Bileşik yabancı anahtar (ADR-0020): üyelik ile departman aynı tenant'ta olmak zorundadır. Üyelik silinince
        // departman üyeliği de silinir; departman silinmediği için kısıtlıdır.
        builder.Property(m => m.MembershipId).IsRequired();
        builder.HasOne<Membership>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.MembershipId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<BusinessUnit>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.BusinessUnitId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
