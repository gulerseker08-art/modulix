using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Persistence;

internal sealed class CorrectiveActionConfiguration : IEntityTypeConfiguration<CorrectiveAction>
{
    public void Configure(EntityTypeBuilder<CorrectiveAction> builder)
    {
        SupportTables.ConfigureCommon(builder, "CorrectiveActions");
        builder.Property(a => a.Title).HasMaxLength(CorrectiveAction.TitleMaxLength).IsRequired();
        builder.Property(a => a.Status).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(a => a.Note).HasMaxLength(CorrectiveAction.NoteMaxLength);
        builder.Property(a => a.DueDate).HasColumnType("date");

        builder.HasIndex(a => new { a.TenantId, a.TicketId });
        builder.HasIndex(a => new { a.TenantId, a.Status, a.DueDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_CorrectiveActions_Status", "[Status] IN ('Open','Done','Cancelled')"));
        // Kapanmış aksiyonun kapanış anı ve notu vardır; açık aksiyonun kapanış anı yoktur.
        builder.ToTable(t => t.HasCheckConstraint("CK_CorrectiveActions_Closure",
            "([Status] = 'Open' AND [ClosedAt] IS NULL) OR ([Status] <> 'Open' AND [ClosedAt] IS NOT NULL AND [Note] IS NOT NULL)"));

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(a => new { a.TenantId, a.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
