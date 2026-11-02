using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Persistence;

internal static class MarketingTables
{
    public const string Schema = MarketingModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : MarketingEntity
    {
        builder.ToTable(table, Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.Property(e => e.CustomFields);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        MarketingTables.ConfigureCommon(builder, "Segments");
        builder.Property(s => s.Code).HasMaxLength(Segment.CodeMaxLength).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(Segment.NameMaxLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(Segment.DescriptionMaxLength);
        builder.Property(s => s.Target).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Kind).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Purpose).HasMaxLength(Segment.PurposeMaxLength);
        builder.Property(s => s.Status).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Draft);
        builder.Property(s => s.LastError).HasMaxLength(Segment.ErrorMaxLength);

        // Benzersizlik tenant ile birliktedir (CLAUDE.md): kod tenant içinde tekil.
        builder.HasIndex(s => new { s.TenantId, s.Code }).IsUnique().HasDatabaseName("UX_Segments_Code");
        builder.HasIndex(s => new { s.TenantId, s.Status, s.Target });
    }
}

internal sealed class SegmentVersionConfiguration : IEntityTypeConfiguration<SegmentVersion>
{
    public void Configure(EntityTypeBuilder<SegmentVersion> builder)
    {
        MarketingTables.ConfigureCommon(builder, "SegmentVersions");
        builder.Property(v => v.Definition).IsRequired();
        builder.HasIndex(v => new { v.TenantId, v.SegmentId, v.VersionNo }).IsUnique().HasDatabaseName("UX_SegmentVersions_No");
        builder.HasOne<Segment>().WithMany().HasForeignKey(v => new { v.TenantId, v.SegmentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SegmentMemberConfiguration : IEntityTypeConfiguration<SegmentMember>
{
    public void Configure(EntityTypeBuilder<SegmentMember> builder)
    {
        MarketingTables.ConfigureCommon(builder, "SegmentMembers");
        builder.Property(m => m.Kind).HasMaxLength(20).IsRequired();
        builder.Property(m => m.Reason).HasMaxLength(SegmentMember.ReasonMaxLength);

        // Aynı hedef segmentte bir kez (dahil ya da hariç) bulunur.
        builder.HasIndex(m => new { m.TenantId, m.SegmentId, m.TargetId }).IsUnique().HasDatabaseName("UX_SegmentMembers_Target");
        builder.HasOne<Segment>().WithMany().HasForeignKey(m => new { m.TenantId, m.SegmentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        MarketingTables.ConfigureCommon(builder, "Campaigns");
        builder.Property(c => c.Code).HasMaxLength(Campaign.CodeMaxLength).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(Campaign.NameMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(Campaign.DescriptionMaxLength);
        builder.Property(c => c.Purpose).HasMaxLength(Campaign.PurposeMaxLength);
        builder.Property(c => c.Target).HasMaxLength(20).IsRequired();
        builder.Property(c => c.Channel).HasMaxLength(20).IsRequired();
        builder.Property(c => c.RecipientRule).HasMaxLength(20).IsRequired();
        builder.Property(c => c.RecipientRole).HasMaxLength(Campaign.RoleMaxLength);
        builder.Property(c => c.Status).HasMaxLength(20).IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.Code }).IsUnique().HasDatabaseName("UX_Campaigns_Code");
        builder.HasIndex(c => new { c.TenantId, c.Status });
    }
}

internal sealed class CampaignAudienceEntryConfiguration : IEntityTypeConfiguration<CampaignAudienceEntry>
{
    public void Configure(EntityTypeBuilder<CampaignAudienceEntry> builder)
    {
        MarketingTables.ConfigureCommon(builder, "CampaignAudienceEntries");
        builder.Property(e => e.Mode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(CampaignAudienceEntry.ReasonMaxLength);

        // Aynı segment ya da hedef bir kampanyada bir kez bulunur (dahil ya da hariç).
        builder.HasIndex(e => new { e.TenantId, e.CampaignId, e.SegmentId }).IsUnique().HasFilter("[SegmentId] IS NOT NULL").HasDatabaseName("UX_CampaignAudience_Segment");
        builder.HasIndex(e => new { e.TenantId, e.CampaignId, e.TargetId }).IsUnique().HasFilter("[TargetId] IS NOT NULL").HasDatabaseName("UX_CampaignAudience_Target");
        builder.ToTable(t => t.HasCheckConstraint("CK_CampaignAudience_OneSource", "([SegmentId] IS NULL AND [TargetId] IS NOT NULL) OR ([SegmentId] IS NOT NULL AND [TargetId] IS NULL)"));
        builder.HasOne<Campaign>().WithMany().HasForeignKey(e => new { e.TenantId, e.CampaignId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Segment>().WithMany().HasForeignKey(e => new { e.TenantId, e.SegmentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AudienceSnapshotConfiguration : IEntityTypeConfiguration<AudienceSnapshot>
{
    public void Configure(EntityTypeBuilder<AudienceSnapshot> builder)
    {
        MarketingTables.ConfigureCommon(builder, "AudienceSnapshots");
        builder.Property(s => s.Target).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Channel).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Exclusions).IsRequired();
        builder.Property(s => s.Unresolved).IsRequired();
        builder.Property(s => s.Sources).IsRequired();
        builder.Property(s => s.Recipients).IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.CampaignId, s.TakenAt });
        builder.HasOne<Campaign>().WithMany().HasForeignKey(s => new { s.TenantId, s.CampaignId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
