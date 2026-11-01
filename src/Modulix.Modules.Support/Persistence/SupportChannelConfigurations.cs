using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Persistence;

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        SupportTables.ConfigureCommon(builder, "Attachments");
        builder.Property(a => a.FileName).HasMaxLength(TicketAttachment.FileNameMaxLength).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(TicketAttachment.ContentTypeMaxLength).IsRequired();
        builder.Property(a => a.Sha256).HasMaxLength(TicketAttachment.HashLength).IsRequired();
        builder.Property(a => a.ScanStatus).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(a => a.Content).HasColumnType("varbinary(max)").IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.TicketId });
        builder.HasIndex(a => new { a.TenantId, a.MessageId });
        // Aynı mesaja aynı içerik iki kez eklenmez.
        builder.HasIndex(a => new { a.TenantId, a.MessageId, a.Sha256 }).IsUnique().HasDatabaseName("UX_Attachments_Content");
        builder.ToTable(t => t.HasCheckConstraint("CK_Attachments_Size", "[SizeBytes] > 0 AND [SizeBytes] <= 5242880"));

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(a => new { a.TenantId, a.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TicketMessage>().WithMany().HasForeignKey(a => new { a.TenantId, a.MessageId })
            .HasPrincipalKey(m => new { m.TenantId, m.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SatisfactionSurveyConfiguration : IEntityTypeConfiguration<SatisfactionSurvey>
{
    public void Configure(EntityTypeBuilder<SatisfactionSurvey> builder)
    {
        SupportTables.ConfigureCommon(builder, "Surveys");
        builder.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.RecipientEmail).HasMaxLength(SatisfactionSurvey.EmailMaxLength);
        builder.Property(s => s.Comment).HasMaxLength(SatisfactionSurvey.CommentMaxLength);

        builder.HasIndex(s => new { s.TenantId, s.TicketId, s.Cycle }).IsUnique().HasDatabaseName("UX_Surveys_Cycle");
        // Belirteç özeti genel anket uçlarında tenant adı ile birlikte aranır; yine de tenant içinde benzersizdir.
        builder.HasIndex(s => new { s.TenantId, s.TokenHash }).IsUnique().HasDatabaseName("UX_Surveys_Token");
        builder.HasIndex(s => new { s.TenantId, s.AnsweredAt, s.Rating });
        builder.ToTable(t => t.HasCheckConstraint("CK_Surveys_Rating", "[Rating] IS NULL OR ([Rating] BETWEEN 1 AND 5)"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Surveys_Answer", "([AnsweredAt] IS NULL AND [Rating] IS NULL) OR ([AnsweredAt] IS NOT NULL AND [Rating] IS NOT NULL)"));

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(s => new { s.TenantId, s.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketLinkConfiguration : IEntityTypeConfiguration<TicketLink>
{
    public void Configure(EntityTypeBuilder<TicketLink> builder)
    {
        SupportTables.ConfigureCommon(builder, "TicketLinks");
        builder.Property(l => l.EntityKey).HasMaxLength(TicketLink.EntityKeyMaxLength).IsRequired();
        builder.Property(l => l.Label).HasMaxLength(TicketLink.LabelMaxLength).IsRequired();

        builder.HasIndex(l => new { l.TenantId, l.TicketId, l.EntityKey, l.RecordId }).IsUnique().HasDatabaseName("UX_TicketLinks_Record");
        // Karşı modülün sayfalarındaki "ilişkili talepler" listesi için ters arama.
        builder.HasIndex(l => new { l.TenantId, l.EntityKey, l.RecordId });
        builder.ToTable(t => t.HasCheckConstraint("CK_TicketLinks_Kind", "[EntityKey] IN ('contracts.contract','orders.order','orders.delivery')"));

        // RecordId başka modülün kaydıdır: yabancı anahtar yoktur (bağlanırken boru hattıyla doğrulanır).
        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(l => new { l.TenantId, l.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
