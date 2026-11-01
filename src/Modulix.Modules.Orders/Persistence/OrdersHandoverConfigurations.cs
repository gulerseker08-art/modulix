using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Persistence;

internal sealed class OrderHandoverConfiguration : IEntityTypeConfiguration<OrderHandover>
{
    public void Configure(EntityTypeBuilder<OrderHandover> builder)
    {
        OrdersTables.ConfigureCommon(builder, "Handovers");
        builder.Property(h => h.Status).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(h => h.Priority).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        foreach (var text in new Expression<Func<OrderHandover, string?>>[]
                 {
                     h => h.Scope, h => h.OutOfScope, h => h.Deliverables, h => h.Commitments, h => h.RequiredDocuments, h => h.CustomerPrerequisites,
                     h => h.PaymentConditions, h => h.Risks, h => h.SpecialRequests, h => h.Dependencies, h => h.CompletionCriteria, h => h.AcceptanceCriteria, h => h.InfoRequest,
                 })
        {
            builder.Property(text).HasMaxLength(OrderHandover.TextMaxLength);
        }

        builder.Property(h => h.Location).HasMaxLength(SalesOrder.AddressMaxLength);
        builder.Property(h => h.ContactName).HasMaxLength(OrderHandover.ShortMaxLength);
        builder.Property(h => h.ContactPhone).HasMaxLength(OrderHandover.ShortMaxLength);
        builder.Property(h => h.ContactEmail).HasMaxLength(254);
        builder.Property(h => h.ResponsibleTeam).HasMaxLength(OrderHandover.ShortMaxLength);
        builder.Property(h => h.TargetStartDate).HasColumnType("date");
        builder.Property(h => h.TargetEndDate).HasColumnType("date");

        // Sipariş başına tek devir kaydı (eşzamanlı ikinci oluşturma benzersiz anahtara çarpar).
        builder.HasIndex(h => new { h.TenantId, h.OrderId }).IsUnique().HasDatabaseName("UX_Handovers_Order");
        builder.HasIndex(h => new { h.TenantId, h.Status, h.SentAt });
        builder.ToTable(t => t.HasCheckConstraint("CK_Handovers_Status", "[Status] IN ('Preparing','Ready','Sent','InfoRequested','Accepted')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Handovers_Dates", "[TargetEndDate] IS NULL OR [TargetStartDate] IS NULL OR [TargetEndDate] >= [TargetStartDate]"));

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(h => new { h.TenantId, h.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderHandoverItemConfiguration : IEntityTypeConfiguration<OrderHandoverItem>
{
    public void Configure(EntityTypeBuilder<OrderHandoverItem> builder)
    {
        OrdersTables.ConfigureCommon(builder, "HandoverItems");
        builder.Property(i => i.Text).HasMaxLength(OrderHandoverItem.TextMaxLength).IsRequired();
        builder.Property(i => i.Note).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.HasIndex(i => new { i.TenantId, i.HandoverId, i.Position });

        builder.HasOne<OrderHandover>().WithMany().HasForeignKey(i => new { i.TenantId, i.HandoverId })
            .HasPrincipalKey(h => new { h.TenantId, h.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderHandoverEventConfiguration : IEntityTypeConfiguration<OrderHandoverEvent>
{
    public void Configure(EntityTypeBuilder<OrderHandoverEvent> builder)
    {
        OrdersTables.ConfigureCommon(builder, "HandoverEvents");
        builder.Property(e => e.Kind).HasMaxLength(SalesOrder.StatusMaxLength * 2).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(OrderHandover.TextMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.HandoverId, e.CreatedAt });
        builder.HasIndex(e => new { e.TenantId, e.OrderId });

        builder.HasOne<OrderHandover>().WithMany().HasForeignKey(e => new { e.TenantId, e.HandoverId })
            .HasPrincipalKey(h => new { h.TenantId, h.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
