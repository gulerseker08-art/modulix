using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Persistence;

internal sealed class PriorityMatrixEntryConfiguration : IEntityTypeConfiguration<PriorityMatrixEntry>
{
    public void Configure(EntityTypeBuilder<PriorityMatrixEntry> builder)
    {
        SupportTables.ConfigureCommon(builder, "PriorityMatrix");
        builder.Property(e => e.Impact).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(e => e.Urgency).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(e => e.Priority).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();

        // Hücre başına tek kayıt.
        builder.HasIndex(e => new { e.TenantId, e.Impact, e.Urgency }).IsUnique().HasDatabaseName("UX_PriorityMatrix_Cell");
        builder.ToTable(t => t.HasCheckConstraint("CK_PriorityMatrix_Impact", "[Impact] IN ('Low','Medium','High')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_PriorityMatrix_Urgency", "[Urgency] IN ('Low','Medium','High')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_PriorityMatrix_Priority", "[Priority] IN ('Low','Normal','High','Critical')"));
    }
}

internal sealed class TicketRoutingRuleConfiguration : IEntityTypeConfiguration<TicketRoutingRule>
{
    public void Configure(EntityTypeBuilder<TicketRoutingRule> builder)
    {
        SupportTables.ConfigureCommon(builder, "RoutingRules");
        builder.Property(r => r.Name).HasMaxLength(TicketRoutingRule.NameMaxLength).IsRequired();
        builder.Property(r => r.Channel).HasMaxLength(SupportTicket.StatusMaxLength);
        builder.Property(r => r.TypeKey).HasMaxLength(SupportTicket.KeyMaxLength);
        builder.Property(r => r.Priority).HasMaxLength(SupportTicket.StatusMaxLength);

        builder.HasIndex(r => new { r.TenantId, r.Name }).IsUnique().HasDatabaseName("UX_RoutingRules_Name");
        builder.HasIndex(r => new { r.TenantId, r.IsActive, r.Order });
        builder.ToTable(t => t.HasCheckConstraint("CK_RoutingRules_Action", "[QueueId] IS NOT NULL OR [OwnerId] IS NOT NULL"));
        builder.ToTable(t => t.HasCheckConstraint("CK_RoutingRules_Priority", "[Priority] IS NULL OR [Priority] IN ('Low','Normal','High','Critical')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_RoutingRules_Channel", "[Channel] IS NULL OR [Channel] IN ('User','Phone','Email','Web','Portal','Api')"));

        builder.HasOne<SupportQueue>().WithMany().HasForeignKey(r => new { r.TenantId, r.QueueId })
            .HasPrincipalKey(q => new { q.TenantId, q.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TicketCategory>().WithMany().HasForeignKey(r => new { r.TenantId, r.CategoryId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(r => new { r.TenantId, r.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketTaskConfiguration : IEntityTypeConfiguration<TicketTask>
{
    public void Configure(EntityTypeBuilder<TicketTask> builder)
    {
        SupportTables.ConfigureCommon(builder, "TicketTasks");
        builder.Property(t => t.Title).HasMaxLength(TicketTask.TitleMaxLength).IsRequired();

        builder.HasIndex(t => new { t.TenantId, t.TicketId, t.IsDone });

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(t => new { t.TenantId, t.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
