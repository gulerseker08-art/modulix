using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        CrmTables.ConfigureCommon(builder, "Quotes");
        builder.Property(q => q.QuoteNumber).HasMaxLength(Quote.NumberMaxLength).IsRequired();
        builder.Property(q => q.Title).HasMaxLength(Quote.TitleMaxLength).IsRequired();
        builder.Property(q => q.Description).HasMaxLength(Quote.TextMaxLength);
        builder.Property(q => q.Status).HasMaxLength(Quote.StatusMaxLength).IsRequired();
        builder.Property(q => q.ApprovalStatus).HasMaxLength(Quote.StatusMaxLength).IsRequired();
        builder.Property(q => q.DeliveryStatus).HasMaxLength(Quote.StatusMaxLength).IsRequired();
        builder.Property(q => q.StatusReason).HasMaxLength(Quote.ReasonMaxLength);
        builder.Property(q => q.IssueDate).HasColumnType("date");
        builder.Property(q => q.Currency).HasMaxLength(Quote.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(q => q.ReportingCurrency).HasMaxLength(Quote.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(q => q.ExchangeRate).HasPrecision(28, 10);
        builder.Property(q => q.ExchangeRateDate).HasColumnType("date");
        builder.Property(q => q.DocumentDiscountKind).HasMaxLength(10).IsRequired();
        builder.Property(q => q.DocumentDiscountValue).HasPrecision(18, 4);
        builder.Property(q => q.PaymentTerms).HasMaxLength(Quote.TextMaxLength);
        builder.Property(q => q.DeliveryTerms).HasMaxLength(Quote.TextMaxLength);
        builder.Property(q => q.CommercialTerms).HasMaxLength(Quote.TextMaxLength);
        builder.Property(q => q.InternalNotes).HasMaxLength(Quote.TextMaxLength);
        builder.Property(q => q.ListTotal).HasPrecision(18, 2);
        builder.Property(q => q.DiscountTotal).HasPrecision(18, 2);
        builder.Property(q => q.NetTotal).HasPrecision(18, 2);
        builder.Property(q => q.TaxTotal).HasPrecision(18, 2);
        builder.Property(q => q.GrossTotal).HasPrecision(18, 2);
        builder.Property(q => q.OptionalNet).HasPrecision(18, 2);
        builder.Property(q => q.CustomerNameSnapshot).HasMaxLength(300);

        builder.HasIndex(q => new { q.TenantId, q.QuoteNumber, q.RevisionNo }).IsUnique().HasDatabaseName("UX_Quotes_NumberRevision");
        builder.HasIndex(q => new { q.TenantId, q.FamilyId, q.RevisionNo }).IsUnique().HasDatabaseName("UX_Quotes_FamilyRevision");
        // Aynı ailede çelişen iki sürüm aynı anda kabul edilemez: yarış olsa bile veritabanı ikincisini reddeder.
        builder.HasIndex(q => new { q.TenantId, q.FamilyId }).IsUnique().HasFilter("[Status] = 'Accepted'").HasDatabaseName("UX_Quotes_FamilyAccepted");
        builder.HasIndex(q => new { q.TenantId, q.CompanyId });
        builder.HasIndex(q => new { q.TenantId, q.OpportunityId });
        builder.HasIndex(q => new { q.TenantId, q.OwnerId, q.Status });
        builder.HasIndex(q => new { q.TenantId, q.Status, q.ValidUntil });
        builder.ToTable(t => t.HasCheckConstraint("CK_Quotes_Revision", "[RevisionNo] >= 1"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Quotes_Totals", "[NetTotal] >= 0 AND [TaxTotal] >= 0 AND [GrossTotal] >= 0"));

        builder.HasOne<Company>().WithMany().HasForeignKey(q => new { q.TenantId, q.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(q => new { q.TenantId, q.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Opportunity>().WithMany().HasForeignKey(q => new { q.TenantId, q.OpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PriceList>().WithMany().HasForeignKey(q => new { q.TenantId, q.PriceListId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuoteLineConfiguration : IEntityTypeConfiguration<QuoteLine>
{
    public void Configure(EntityTypeBuilder<QuoteLine> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteLines");
        builder.Property(l => l.GroupName).HasMaxLength(QuoteLine.NameMaxLength);
        builder.Property(l => l.Kind).HasMaxLength(QuoteLine.KindMaxLength).IsRequired();
        builder.Property(l => l.ProductCode).HasMaxLength(QuoteLine.CodeMaxLength);
        builder.Property(l => l.Name).HasMaxLength(QuoteLine.NameMaxLength).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(QuoteLine.TextMaxLength);
        builder.Property(l => l.Quantity).HasPrecision(28, 10);
        builder.Property(l => l.UnitName).HasMaxLength(100);
        builder.Property(l => l.UnitPrice).HasPrecision(28, 10);
        builder.Property(l => l.DiscountKind).HasMaxLength(10).IsRequired();
        builder.Property(l => l.DiscountValue).HasPrecision(18, 4);
        builder.Property(l => l.TaxRatePercent).HasPrecision(9, 4);
        builder.Property(l => l.TaxCode).HasMaxLength(30);
        builder.Property(l => l.Role).HasMaxLength(20).IsRequired();
        builder.Property(l => l.RecurringPeriod).HasMaxLength(20);
        builder.Property(l => l.UnitCost).HasPrecision(28, 10);
        builder.Property(l => l.ListAmount).HasPrecision(18, 2);
        builder.Property(l => l.LineDiscount).HasPrecision(18, 2);
        builder.Property(l => l.DocumentDiscountShare).HasPrecision(18, 2);
        builder.Property(l => l.Net).HasPrecision(18, 2);
        builder.Property(l => l.Tax).HasPrecision(18, 2);
        builder.Property(l => l.Gross).HasPrecision(18, 2);

        builder.HasIndex(l => new { l.TenantId, l.QuoteId, l.StableKey }).IsUnique().HasDatabaseName("UX_QuoteLines_QuoteKey");
        builder.HasIndex(l => new { l.TenantId, l.QuoteId, l.Position });
        builder.HasIndex(l => new { l.TenantId, l.ProductId });
        builder.ToTable(t => t.HasCheckConstraint("CK_QuoteLines_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [DiscountValue] >= 0"));
        builder.ToTable(t => t.HasCheckConstraint("CK_QuoteLines_Tax", "[TaxRatePercent] >= 0 AND [TaxRatePercent] <= 100"));

        builder.HasOne<Quote>().WithMany().HasForeignKey(l => new { l.TenantId, l.QuoteId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(l => new { l.TenantId, l.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuoteEventConfiguration : IEntityTypeConfiguration<QuoteEvent>
{
    public void Configure(EntityTypeBuilder<QuoteEvent> builder)
    {
        CrmTables.ConfigureCommon(builder, "QuoteEvents");
        builder.Property(e => e.Kind).HasMaxLength(40).IsRequired();
        builder.Property(e => e.FromStatus).HasMaxLength(Quote.StatusMaxLength);
        builder.Property(e => e.ToStatus).HasMaxLength(Quote.StatusMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(Quote.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.QuoteId, e.CreatedAt });
        builder.HasOne<Quote>().WithMany().HasForeignKey(e => new { e.TenantId, e.QuoteId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
