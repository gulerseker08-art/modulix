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
        builder.Property(c => c.UtmSource).HasMaxLength(Campaign.UtmMaxLength);
        builder.Property(c => c.UtmMedium).HasMaxLength(Campaign.UtmMaxLength);
        builder.Property(c => c.UtmCampaign).HasMaxLength(Campaign.UtmMaxLength);
        builder.Property(c => c.Budget).HasPrecision(18, 2);
        builder.Property(c => c.ActualCost).HasPrecision(18, 2);
        builder.Property(c => c.CostCurrency).HasMaxLength(3).IsRequired();
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

internal sealed class CampaignContentConfiguration : IEntityTypeConfiguration<CampaignContent>
{
    public void Configure(EntityTypeBuilder<CampaignContent> builder)
    {
        MarketingTables.ConfigureCommon(builder, "CampaignContents");
        builder.Property(c => c.Subject).HasMaxLength(CampaignContent.SubjectMaxLength);
        builder.Property(c => c.Body).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.CampaignId, c.VersionNo }).IsUnique().HasDatabaseName("UX_CampaignContents_No");
        builder.HasOne<Campaign>().WithMany().HasForeignKey(c => new { c.TenantId, c.CampaignId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignRunConfiguration : IEntityTypeConfiguration<CampaignRun>
{
    public void Configure(EntityTypeBuilder<CampaignRun> builder)
    {
        MarketingTables.ConfigureCommon(builder, "CampaignRuns");
        builder.Property(r => r.Status).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(CampaignRun.NoteMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.CampaignId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.Status, r.ScheduledAt });
        builder.HasOne<Campaign>().WithMany().HasForeignKey(r => new { r.TenantId, r.CampaignId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AudienceSnapshot>().WithMany().HasForeignKey(r => new { r.TenantId, r.SnapshotId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CampaignContent>().WithMany().HasForeignKey(r => new { r.TenantId, r.ContentId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignDeliveryConfiguration : IEntityTypeConfiguration<CampaignDelivery>
{
    public void Configure(EntityTypeBuilder<CampaignDelivery> builder)
    {
        builder.ToTable("CampaignDeliveries", MarketingTables.Schema);
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.IdempotencyKey).HasMaxLength(CampaignDelivery.KeyLength).IsRequired();
        builder.Property(d => d.Status).HasMaxLength(20).IsRequired();
        builder.Property(d => d.Code).HasMaxLength(CampaignDelivery.CodeMaxLength);
        builder.Property(d => d.ProviderMessageId).HasMaxLength(CampaignDelivery.ProviderIdMaxLength);

        // Aynı çalıştırmada aynı alıcıya (anahtar) ikinci gönderim satırı açılamaz: tekrar başlatma ve işçi yeniden başlaması çift satır üretmez.
        builder.HasIndex(d => new { d.TenantId, d.RunId, d.IdempotencyKey }).IsUnique().HasDatabaseName("UX_CampaignDeliveries_Key");
        builder.HasIndex(d => new { d.TenantId, d.RunId, d.Status });
        builder.HasOne<CampaignRun>().WithMany().HasForeignKey(d => new { d.TenantId, d.RunId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignResponseConfiguration : IEntityTypeConfiguration<CampaignResponse>
{
    public void Configure(EntityTypeBuilder<CampaignResponse> builder)
    {
        MarketingTables.ConfigureCommon(builder, "CampaignResponses");
        builder.Property(r => r.Kind).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(CampaignResponse.NoteMaxLength);
        builder.Property(r => r.ClosedReason).HasMaxLength(CampaignResponse.ReasonMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.CampaignId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.OwnerId, r.Status });
        builder.HasIndex(r => new { r.TenantId, r.DeliveryId });
        builder.HasOne<Campaign>().WithMany().HasForeignKey(r => new { r.TenantId, r.CampaignId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CampaignRun>().WithMany().HasForeignKey(r => new { r.TenantId, r.RunId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignTestSendConfiguration : IEntityTypeConfiguration<CampaignTestSend>
{
    public void Configure(EntityTypeBuilder<CampaignTestSend> builder)
    {
        builder.ToTable("CampaignTestSends", MarketingTables.Schema);
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TenantId).IsRequired();
        builder.Property(t => t.Outcome).HasMaxLength(CampaignTestSend.OutcomeMaxLength).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(CampaignTestSend.CodeMaxLength);
        builder.HasIndex(t => new { t.TenantId, t.CampaignId, t.CreatedAt });
    }
}
