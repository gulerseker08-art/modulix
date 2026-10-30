using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class QuoteDocumentConfiguration : IEntityTypeConfiguration<QuoteDocument>
{
    public void Configure(EntityTypeBuilder<QuoteDocument> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteDocuments");
        builder.Property(d => d.FileName).HasMaxLength(QuoteDocument.FileNameMaxLength).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(60).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(QuoteDocument.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.Snapshot).IsRequired();
        builder.Property(d => d.Content).HasColumnType("varbinary(max)").IsRequired();
        // Teklif sürümü (revizyon) başına tek resmî belge; eşzamanlı ikinci üretim burada elenir.
        builder.HasIndex(d => new { d.TenantId, d.QuoteId }).IsUnique().HasDatabaseName("UX_QuoteDocuments_Quote");
        builder.HasOne<Quote>().WithMany().HasForeignKey(d => new { d.TenantId, d.QuoteId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuoteShareLinkConfiguration : IEntityTypeConfiguration<QuoteShareLink>
{
    public void Configure(EntityTypeBuilder<QuoteShareLink> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteShareLinks");
        builder.Property(l => l.TokenHash).HasMaxLength(QuoteDocument.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(l => l.TokenHint).HasMaxLength(8).IsRequired();
        builder.Property(l => l.RecipientName).HasMaxLength(QuoteShareLink.NameMaxLength);
        builder.Property(l => l.RecipientEmail).HasMaxLength(QuoteShareLink.EmailMaxLength);
        builder.Property(l => l.RevokeReason).HasMaxLength(Quote.ReasonMaxLength);
        // Belirteç özeti tenant içinde benzersizdir; müşteri isteği bu indeksle tek satıra çözülür.
        builder.HasIndex(l => new { l.TenantId, l.TokenHash }).IsUnique().HasDatabaseName("UX_QuoteShareLinks_Token");
        builder.HasIndex(l => new { l.TenantId, l.QuoteId, l.CreatedAt });
        builder.HasOne<Quote>().WithMany().HasForeignKey(l => new { l.TenantId, l.QuoteId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuoteDocument>().WithMany().HasForeignKey(l => new { l.TenantId, l.DocumentId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuoteDecisionConfiguration : IEntityTypeConfiguration<QuoteDecision>
{
    public void Configure(EntityTypeBuilder<QuoteDecision> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteDecisions");
        builder.Property(d => d.DocumentSha256).HasMaxLength(QuoteDocument.HashLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.Decision).HasMaxLength(20).IsRequired();
        builder.Property(d => d.SignerName).HasMaxLength(QuoteDecision.NameMaxLength).IsRequired();
        builder.Property(d => d.SignerEmail).HasMaxLength(QuoteShareLink.EmailMaxLength);
        builder.Property(d => d.SignerTitle).HasMaxLength(QuoteDecision.NameMaxLength);
        builder.Property(d => d.Reason).HasMaxLength(Quote.ReasonMaxLength);
        builder.Property(d => d.TermsStatement).HasMaxLength(QuoteDecision.StatementMaxLength);
        builder.Property(d => d.ClientAddressMasked).HasMaxLength(60);
        builder.Property(d => d.UserAgent).HasMaxLength(QuoteDecision.AgentMaxLength);
        builder.HasIndex(d => new { d.TenantId, d.QuoteId, d.CreatedAt });
        builder.ToTable(t => t.HasCheckConstraint("CK_QuoteDecisions_Decision", "[Decision] IN ('Accepted','Rejected')"));
        builder.HasOne<Quote>().WithMany().HasForeignKey(d => new { d.TenantId, d.QuoteId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuoteShareLink>().WithMany().HasForeignKey(d => new { d.TenantId, d.ShareLinkId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuoteDocument>().WithMany().HasForeignKey(d => new { d.TenantId, d.DocumentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
