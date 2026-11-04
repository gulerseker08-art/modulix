using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.FieldRules;
using Modulix.Core.Sectors;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class RulePackageConfiguration : IEntityTypeConfiguration<RulePackage>
{
    public void Configure(EntityTypeBuilder<RulePackage> builder)
    {
        // Platform kataloğu (ADR-0029 K8): TenantId taşımaz, RLS dışıdır; platform yöneticisi yönetir.
        builder.ToTable("RulePackages", DatabaseSchemas.Platform);
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Layer).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(p => p.Code).IsUnique();
        builder.HasIndex(p => p.NodeId);
        builder.HasOne<ActivityNode>().WithMany().HasForeignKey(p => p.NodeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RulePackageVersionConfiguration : IEntityTypeConfiguration<RulePackageVersion>
{
    public void Configure(EntityTypeBuilder<RulePackageVersion> builder)
    {
        builder.ToTable("RulePackageVersions", DatabaseSchemas.Platform);
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(v => v.RulesJson).IsRequired();
        builder.Property(v => v.Note).HasMaxLength(500);
        builder.Property(v => v.Revision).IsRequired().IsConcurrencyToken();
        builder.HasIndex(v => new { v.PackageId, v.Number }).IsUnique();

        // Her paketin en çok bir taslağı ve en çok bir yayınlanmış sürümü olur (iki yönetici aynı anda yayınlayamaz).
        builder.HasIndex(v => v.PackageId, "UX_RulePackageVersions_Draft").IsUnique().HasFilter("[Status] = N'Draft'");
        builder.HasIndex(v => v.PackageId, "UX_RulePackageVersions_Published").IsUnique().HasFilter("[Status] = N'Published'");
        builder.HasOne<RulePackage>().WithMany().HasForeignKey(v => v.PackageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TenantRuleAdaptationConfiguration : IEntityTypeConfiguration<TenantRuleAdaptation>
{
    public void Configure(EntityTypeBuilder<TenantRuleAdaptation> builder)
    {
        // Tenant verisi (ADR-0029 K8): çekirdek şemada; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("RuleAdaptations", DatabaseSchemas.Core);
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.RulesJson).IsRequired();
        builder.Property(a => a.DisabledKeysJson).IsRequired();
        builder.Property(a => a.Revision).IsRequired().IsConcurrencyToken();
        builder.HasIndex(a => a.TenantId).IsUnique();
        builder.HasAlternateKey(a => new { a.TenantId, a.Id });
    }
}
