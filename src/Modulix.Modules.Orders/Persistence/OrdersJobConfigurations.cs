using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Persistence;

internal sealed class OperationJobConfiguration : IEntityTypeConfiguration<OperationJob>
{
    public void Configure(EntityTypeBuilder<OperationJob> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OperationJobs");
        builder.Property(j => j.Number).HasMaxLength(OperationJob.NumberMaxLength).IsRequired();
        builder.Property(j => j.Title).HasMaxLength(OperationJob.TitleMaxLength).IsRequired();
        builder.Property(j => j.Kind).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(j => j.GroupKey).HasMaxLength(OperationJob.KeyMaxLength).IsUnicode(false);
        builder.Property(j => j.Status).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(j => j.Priority).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();
        builder.Property(j => j.TeamName).HasMaxLength(OrderHandover.ShortMaxLength);
        builder.Property(j => j.PlannedStartDate).HasColumnType("date");
        builder.Property(j => j.PlannedEndDate).HasColumnType("date");
        builder.Property(j => j.Location).HasMaxLength(SalesOrder.AddressMaxLength);
        builder.Property(j => j.Notes).HasMaxLength(OperationJob.TextMaxLength);
        builder.Property(j => j.CancelReason).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.Property(j => j.Source).HasMaxLength(SalesOrder.StatusMaxLength).IsRequired();

        builder.HasIndex(j => new { j.TenantId, j.Number }).IsUnique().HasDatabaseName("UX_OperationJobs_Number");
        // Aynı siparişte aynı üretim grubu için tek açık iş: yeniden deneme ya da tekrar kabul mükerrer iş üretemez.
        builder.HasIndex(j => new { j.TenantId, j.OrderId, j.GroupKey }).IsUnique().HasFilter("[GroupKey] IS NOT NULL AND [Status] <> 'Cancelled'")
            .HasDatabaseName("UX_OperationJobs_Group");
        builder.HasIndex(j => new { j.TenantId, j.Status, j.PlannedEndDate });
        builder.HasIndex(j => new { j.TenantId, j.ResponsibleUserId, j.Status });
        builder.HasIndex(j => new { j.TenantId, j.OrderId });
        builder.ToTable(t => t.HasCheckConstraint("CK_OperationJobs_Status", "[Status] IN ('AwaitingAssignment','Planned','InProgress','Completed','Cancelled')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_OperationJobs_Dates", "[PlannedEndDate] IS NULL OR [PlannedStartDate] IS NULL OR [PlannedEndDate] >= [PlannedStartDate]"));

        builder.HasOne<SalesOrder>().WithMany().HasForeignKey(j => new { j.TenantId, j.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationJobLineConfiguration : IEntityTypeConfiguration<OperationJobLine>
{
    public void Configure(EntityTypeBuilder<OperationJobLine> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OperationJobLines");
        builder.HasIndex(l => new { l.TenantId, l.JobId, l.OrderLineId }).IsUnique().HasDatabaseName("UX_OperationJobLines_Line");
        builder.HasIndex(l => new { l.TenantId, l.OrderId });

        builder.HasOne<OperationJob>().WithMany().HasForeignKey(l => new { l.TenantId, l.JobId })
            .HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SalesOrderLine>().WithMany().HasForeignKey(l => new { l.TenantId, l.OrderLineId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationJobTaskConfiguration : IEntityTypeConfiguration<OperationJobTask>
{
    public void Configure(EntityTypeBuilder<OperationJobTask> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OperationJobTasks");
        builder.Property(t => t.Title).HasMaxLength(OperationJobTask.TitleMaxLength).IsRequired();
        builder.Property(t => t.Note).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.HasIndex(t => new { t.TenantId, t.JobId, t.Position });

        builder.HasOne<OperationJob>().WithMany().HasForeignKey(t => new { t.TenantId, t.JobId })
            .HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationJobDependencyConfiguration : IEntityTypeConfiguration<OperationJobDependency>
{
    public void Configure(EntityTypeBuilder<OperationJobDependency> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OperationJobDependencies");
        builder.HasIndex(d => new { d.TenantId, d.JobId, d.DependsOnJobId }).IsUnique().HasDatabaseName("UX_OperationJobDependencies_Pair");
        builder.HasIndex(d => new { d.TenantId, d.DependsOnJobId });
        builder.ToTable(t => t.HasCheckConstraint("CK_OperationJobDependencies_Self", "[JobId] <> [DependsOnJobId]"));

        builder.HasOne<OperationJob>().WithMany().HasForeignKey(d => new { d.TenantId, d.JobId })
            .HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperationJob>().WithMany().HasForeignKey(d => new { d.TenantId, d.DependsOnJobId })
            .HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OperationJobEventConfiguration : IEntityTypeConfiguration<OperationJobEvent>
{
    public void Configure(EntityTypeBuilder<OperationJobEvent> builder)
    {
        OrdersTables.ConfigureCommon(builder, "OperationJobEvents");
        builder.Property(e => e.Kind).HasMaxLength(SalesOrder.StatusMaxLength * 2).IsRequired();
        builder.Property(e => e.FromStatus).HasMaxLength(SalesOrder.StatusMaxLength);
        builder.Property(e => e.ToStatus).HasMaxLength(SalesOrder.StatusMaxLength);
        builder.Property(e => e.Note).HasMaxLength(SalesOrder.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.JobId, e.CreatedAt });

        builder.HasOne<OperationJob>().WithMany().HasForeignKey(e => new { e.TenantId, e.JobId })
            .HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
