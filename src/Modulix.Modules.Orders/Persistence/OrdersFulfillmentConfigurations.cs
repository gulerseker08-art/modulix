using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Persistence;

internal sealed class OrderDeliveryConfiguration : IEntityTypeConfiguration<OrderDelivery>
{
    public void Configure(EntityTypeBuilder<OrderDelivery> builder)
    {
        OrdersTables.ConfigureCommon(builder, "Deliveries");
        builder.Property(d => d.Number).HasMaxLength(OrderDelivery.NumberMaxLength).IsRequired();
        builder.Property(d => d.Status).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(d => d.PlannedDate).HasColumnType("date");
        builder.Property(d => d.ActualDate).HasColumnType("date");
        builder.Property(d => d.ReceiverName).HasMaxLength(SalesOrder.NameMaxLength);
        builder.Property(d => d.ReceiverNote).HasMaxLength(OrderDelivery.TextMaxLength);
        builder.Property(d => d.FailureReason).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(d => d.RescheduleReason).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(d => d.Notes).HasMaxLength(OrderDelivery.TextMaxLength);

        builder.HasIndex(d => new { d.TenantId, d.Number }).IsUnique().HasDatabaseName("UX_Deliveries_Number");
        builder.HasIndex(d => new { d.TenantId, d.OrderId });
        builder.HasIndex(d => new { d.TenantId, d.Status, d.PlannedDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_Deliveries_Status", "[Status] IN ('Planned','Delivered','Failed','Cancelled')"));

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(d => new { d.TenantId, d.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderDeliveryLineConfiguration : IEntityTypeConfiguration<OrderDeliveryLine>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryLine> builder)
    {
        OrdersTables.ConfigureCommon(builder, "DeliveryLines");
        builder.Property(l => l.PlannedQuantity).HasPrecision(18, 4);
        builder.Property(l => l.DeliveredQuantity).HasPrecision(18, 4);
        builder.HasIndex(l => new { l.TenantId, l.DeliveryId, l.OrderLineId }).IsUnique().HasDatabaseName("UX_DeliveryLines_Line");
        builder.HasIndex(l => new { l.TenantId, l.OrderId, l.OrderLineId });
        builder.ToTable(t => t.HasCheckConstraint("CK_DeliveryLines_Quantity", "[PlannedQuantity] > 0 AND [DeliveredQuantity] >= 0"));

        builder.HasOne<OrderDelivery>().WithMany().HasForeignKey(l => new { l.TenantId, l.DeliveryId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesOrderLine>().WithMany().HasForeignKey(l => new { l.TenantId, l.OrderLineId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderDeliveryEvidenceConfiguration : IEntityTypeConfiguration<OrderDeliveryEvidence>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryEvidence> builder)
    {
        OrdersTables.ConfigureCommon(builder, "DeliveryEvidence");
        builder.Property(e => e.FileName).HasMaxLength(120).IsRequired();
        builder.Property(e => e.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(e => e.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(e => e.Content).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.DeliveryId });
        builder.ToTable(t => t.HasCheckConstraint("CK_DeliveryEvidence_Size", $"[SizeBytes] > 0 AND [SizeBytes] <= {OrderDeliveryEvidence.MaxBytes}"));

        builder.HasOne<OrderDelivery>().WithMany().HasForeignKey(e => new { e.TenantId, e.DeliveryId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderServiceStartConfiguration : IEntityTypeConfiguration<OrderServiceStart>
{
    public void Configure(EntityTypeBuilder<OrderServiceStart> builder)
    {
        OrdersTables.ConfigureCommon(builder, "ServiceStarts");
        builder.Property(s => s.Status).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(s => s.PlannedStartDate).HasColumnType("date");
        builder.Property(s => s.CustomerConfirmedBy).HasMaxLength(SalesOrder.NameMaxLength);
        builder.Property(s => s.ConfirmationNote).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(s => s.DelayReason).HasMaxLength(SalesOrder.ReasonMaxLength);

        // Hizmet kalemi başına tek başlangıç kaydı (eşzamanlı ikinci oluşturma benzersiz anahtara çarpar).
        builder.HasIndex(s => new { s.TenantId, s.OrderLineId }).IsUnique().HasDatabaseName("UX_ServiceStarts_Line");
        builder.HasIndex(s => new { s.TenantId, s.OrderId });
        builder.HasIndex(s => new { s.TenantId, s.Status, s.PlannedStartDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_ServiceStarts_Status", "[Status] IN ('Planned','Started','Completed')"));

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(s => new { s.TenantId, s.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesOrderLine>().WithMany().HasForeignKey(s => new { s.TenantId, s.OrderLineId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
