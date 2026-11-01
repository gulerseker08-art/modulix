using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Persistence;

// Sipariş tabloları "orders" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo Row-Level Security
// politikasına eklenir (TenantIsolationSql.AddTable). Kayıtlar arası bağlar (TenantId, Id) üzerinden kurulur: başka tenant'ın kaydına
// veritabanı düzeyinde de bağlanılamaz.

internal static class OrdersTables
{
    public const string Schema = OrdersModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : OrdersEntity
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

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        OrdersTables.ConfigureCommon(builder, "Orders");
        builder.Property(o => o.Number).HasMaxLength(SalesOrder.NumberMaxLength).IsRequired();
        builder.Property(o => o.Title).HasMaxLength(SalesOrder.TitleMaxLength).IsRequired();
        builder.Property(o => o.SourceQuoteNumber).HasMaxLength(SalesOrder.NumberMaxLength).IsRequired();
        builder.Property(o => o.SourceQuoteSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(o => o.ContractNumber).HasMaxLength(SalesOrder.NumberMaxLength);
        builder.Property(o => o.ConversionMode).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(o => o.AcceptanceMethod).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(o => o.AcceptedByName).HasMaxLength(SalesOrder.NameMaxLength);
        builder.Property(o => o.AcceptanceNote).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.Status).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(o => o.HandoverStatus).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(o => o.FulfillmentStatus).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(o => o.StatusReason).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(o => o.CustomerName).HasMaxLength(SalesOrder.NameMaxLength);
        builder.Property(o => o.BillingAddress).HasMaxLength(SalesOrder.AddressMaxLength);
        builder.Property(o => o.DeliveryAddress).HasMaxLength(SalesOrder.AddressMaxLength);
        builder.Property(o => o.Currency).HasMaxLength(SalesOrder.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(o => o.ReportingCurrency).HasMaxLength(SalesOrder.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(o => o.ExchangeRate).HasPrecision(18, 8);
        builder.Property(o => o.ExchangeRateDate).HasColumnType("date");
        builder.Property(o => o.PaymentTerms).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.DeliveryTerms).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.CommercialTerms).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.Commitments).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.OperationsNotes).HasMaxLength(SalesOrder.TextMaxLength);
        builder.Property(o => o.Location).HasMaxLength(SalesOrder.AddressMaxLength);
        builder.Property(o => o.PlannedStartDate).HasColumnType("date");
        builder.Property(o => o.PlannedDeliveryDate).HasColumnType("date");
        builder.Property(o => o.NetTotal).HasPrecision(18, 2);
        builder.Property(o => o.TaxTotal).HasPrecision(18, 2);
        builder.Property(o => o.GrossTotal).HasPrecision(18, 2);

        builder.HasIndex(o => new { o.TenantId, o.Number }).IsUnique().HasDatabaseName("UX_Orders_Number");
        builder.HasIndex(o => new { o.TenantId, o.PartyId });
        builder.HasIndex(o => new { o.TenantId, o.SourceQuoteId });
        builder.HasIndex(o => new { o.TenantId, o.SourceOpportunityId });
        builder.HasIndex(o => new { o.TenantId, o.OwnerId, o.Status });
        builder.HasIndex(o => new { o.TenantId, o.Status, o.HandoverStatus });
        builder.HasIndex(o => new { o.TenantId, o.FulfillmentStatus, o.PlannedDeliveryDate });

        builder.ToTable(t => t.HasCheckConstraint("CK_Orders_Status", "[Status] IN ('Draft','PendingApproval','Approved','Cancelled','Closed')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Orders_HandoverStatus", "[HandoverStatus] IN ('NotStarted','Preparing','Ready','Sent','InfoRequested','Accepted')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Orders_FulfillmentStatus", "[FulfillmentStatus] IN ('NotStarted','Planned','Partial','Completed')"));

        // Ortak kayıtlar (ADR-0006): müşteri taraf ve kişi aynı tenant'ta olmalıdır.
        builder.HasOne<Party>().WithMany().HasForeignKey(o => new { o.TenantId, o.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Person>().WithMany().HasForeignKey(o => new { o.TenantId, o.PersonId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesOrderLineConfiguration : IEntityTypeConfiguration<SalesOrderLine>
{
    public void Configure(EntityTypeBuilder<SalesOrderLine> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OrderLines");
        builder.Property(l => l.Kind).HasMaxLength(SalesOrderLine.KindMaxLength).IsRequired();
        builder.Property(l => l.GroupName).HasMaxLength(SalesOrderLine.NameMaxLength);
        builder.Property(l => l.ProductCode).HasMaxLength(SalesOrderLine.CodeMaxLength);
        builder.Property(l => l.Name).HasMaxLength(SalesOrderLine.NameMaxLength).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(SalesOrderLine.TextMaxLength);
        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.AcceptedQuantity).HasPrecision(18, 4);
        builder.Property(l => l.DeliveredQuantity).HasPrecision(18, 4);
        builder.Property(l => l.UnitName).HasMaxLength(SalesOrderLine.UnitMaxLength);
        builder.Property(l => l.UnitPrice).HasPrecision(18, 6);
        builder.Property(l => l.ListAmount).HasPrecision(18, 2);
        builder.Property(l => l.LineDiscount).HasPrecision(18, 2);
        builder.Property(l => l.TaxRatePercent).HasPrecision(9, 4);
        builder.Property(l => l.Net).HasPrecision(18, 2);
        builder.Property(l => l.Tax).HasPrecision(18, 2);
        builder.Property(l => l.Gross).HasPrecision(18, 2);
        builder.Property(l => l.RecurringPeriod).HasMaxLength(SalesOrderLine.KindMaxLength);
        builder.Property(l => l.PlannedDate).HasColumnType("date");

        builder.HasIndex(l => new { l.TenantId, l.OrderId, l.Position });
        // Aynı siparişte aynı teklif kalemi bir kez bulunur; sipariş sınırları arasındaki toplam miktarı tüketim defteri denetler.
        builder.HasIndex(l => new { l.TenantId, l.OrderId, l.SourceQuoteLineKey }).IsUnique().HasDatabaseName("UX_OrderLines_SourceLine");
        builder.HasIndex(l => new { l.TenantId, l.SourceQuoteId, l.SourceQuoteLineKey });
        builder.ToTable(t => t.HasCheckConstraint("CK_OrderLines_Quantity", "[Quantity] > 0 AND [DeliveredQuantity] >= 0 AND [DeliveredQuantity] <= [Quantity]"));

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(l => new { l.TenantId, l.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesOrderEventConfiguration : IEntityTypeConfiguration<SalesOrderEvent>
{
    public void Configure(EntityTypeBuilder<SalesOrderEvent> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OrderEvents");
        builder.Property(e => e.Kind).HasMaxLength(SalesOrder.StatusMaxLength * 2).IsRequired();
        builder.Property(e => e.FromStatus).HasMaxLength(SalesOrder.StatusMaxLength * 4);
        builder.Property(e => e.ToStatus).HasMaxLength(SalesOrder.StatusMaxLength * 4);
        builder.Property(e => e.Reason).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.OrderId, e.CreatedAt });

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(e => new { e.TenantId, e.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class QuoteLineConsumptionConfiguration : IEntityTypeConfiguration<QuoteLineConsumption>
{
    public void Configure(EntityTypeBuilder<QuoteLineConsumption> builder)
    {
        OrdersTables.ConfigureCommon(builder, "QuoteLineConsumptions");
        builder.Property(c => c.AcceptedQuantity).HasPrecision(18, 4);
        builder.Property(c => c.AcceptedNet).HasPrecision(18, 2);
        builder.Property(c => c.AcceptedTax).HasPrecision(18, 2);
        builder.Property(c => c.ConvertedQuantity).HasPrecision(18, 4);
        builder.Property(c => c.ConvertedNet).HasPrecision(18, 2);
        builder.Property(c => c.ConvertedTax).HasPrecision(18, 2);
        builder.Ignore(c => c.RemainingQuantity);

        // Aynı teklif kalemi için tek defter satırı: eşzamanlı ilk dönüşümde ikincisi benzersiz anahtara çarpar.
        builder.HasIndex(c => new { c.TenantId, c.QuoteId, c.LineKey }).IsUnique().HasDatabaseName("UX_QuoteLineConsumptions_Line");
        // Fazla dönüşüm veritabanında da engellenir (uygulama kontrolüne ek güvence).
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_QuoteLineConsumptions_Limit", "[ConvertedQuantity] >= 0 AND [ConvertedQuantity] <= [AcceptedQuantity]"));
    }
}
