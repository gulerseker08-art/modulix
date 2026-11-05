using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Sectors;

namespace Modulix.Infrastructure.Persistence.Configurations;

internal sealed class ActivityNodeConfiguration : IEntityTypeConfiguration<ActivityNode>
{
    public void Configure(EntityTypeBuilder<ActivityNode> builder)
    {
        // Platform kataloğu (ADR-0029): TenantId taşımaz, RLS dışıdır; platform yöneticisi yönetir.
        builder.ToTable("ActivityNodes", DatabaseSchemas.Platform);
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Code).HasMaxLength(ActivityTreeRules.CodeMaxLength).IsRequired();
        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(n => n.Name).HasMaxLength(ActivityTreeRules.NameMaxLength).IsRequired();
        builder.Property(n => n.Description).HasMaxLength(ActivityNode.DescriptionMaxLength);
        builder.Property(n => n.Readiness).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(n => n.Version).IsRequired().IsConcurrencyToken();
        builder.HasIndex(n => n.Code).IsUnique();
        builder.HasIndex(n => n.ParentId);
        builder.HasOne<ActivityNode>().WithMany().HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CapabilityConfiguration : IEntityTypeConfiguration<Capability>
{
    public void Configure(EntityTypeBuilder<Capability> builder)
    {
        builder.ToTable("Capabilities", DatabaseSchemas.Platform);
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Code).HasMaxLength(Capability.CodeMaxLength).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(ActivityTreeRules.NameMaxLength).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();
    }
}

internal sealed class CapabilityDependencyConfiguration : IEntityTypeConfiguration<CapabilityDependency>
{
    public void Configure(EntityTypeBuilder<CapabilityDependency> builder)
    {
        builder.ToTable("CapabilityDependencies", DatabaseSchemas.Platform);
        builder.HasKey(d => new { d.CapabilityId, d.DependsOnId });
        builder.HasOne<Capability>().WithMany().HasForeignKey(d => d.CapabilityId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Capability>().WithMany().HasForeignKey(d => d.DependsOnId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivityNodeCapabilityConfiguration : IEntityTypeConfiguration<ActivityNodeCapability>
{
    public void Configure(EntityTypeBuilder<ActivityNodeCapability> builder)
    {
        builder.ToTable("ActivityNodeCapabilities", DatabaseSchemas.Platform);
        builder.HasKey(c => new { c.NodeId, c.CapabilityId });
        builder.HasOne<ActivityNode>().WithMany().HasForeignKey(c => c.NodeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Capability>().WithMany().HasForeignKey(c => c.CapabilityId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RelationshipRoleConfiguration : IEntityTypeConfiguration<RelationshipRole>
{
    public void Configure(EntityTypeBuilder<RelationshipRole> builder)
    {
        // Platform kataloğu (ADR-0029 K9): TenantId taşımaz, RLS dışıdır.
        builder.ToTable("RelationshipRoles", DatabaseSchemas.Platform);
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Code).HasMaxLength(ActivityTreeRules.CodeMaxLength).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(ActivityTreeRules.NameMaxLength).IsRequired();
        builder.HasIndex(r => r.Code).IsUnique();
    }
}

internal sealed class TenantActivitySelectionConfiguration : IEntityTypeConfiguration<TenantActivitySelection>
{
    public void Configure(EntityTypeBuilder<TenantActivitySelection> builder)
    {
        // Tenant verisi (ADR-0029): çekirdek şemada; Row-Level Security ile korunur (migration: TenantIsolationSql).
        builder.ToTable("ActivitySelections", DatabaseSchemas.Core);
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.SelectedAt).IsRequired();
        builder.HasIndex(s => new { s.TenantId, s.NodeId }).IsUnique();
        builder.HasAlternateKey(s => new { s.TenantId, s.Id });
        builder.HasOne<ActivityNode>().WithMany().HasForeignKey(s => s.NodeId).OnDelete(DeleteBehavior.Restrict);
    }
}
