using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Support.Domain;

namespace Modulix.Modules.Support.Persistence;

// SLA tabloları da tenant verisidir (TenantId + RLS); bağlar (TenantId, Id) üzerinden kurulur.

internal sealed class SlaCalendarConfiguration : IEntityTypeConfiguration<SlaCalendar>
{
    public void Configure(EntityTypeBuilder<SlaCalendar> builder)
    {
        SupportTables.ConfigureCommon(builder, "SlaCalendars");
        builder.Property(c => c.Name).HasMaxLength(SlaCalendar.NameMaxLength).IsRequired();
        builder.Property(c => c.TimeZoneId).HasMaxLength(SlaCalendar.ZoneMaxLength).IsRequired();
        builder.Property(c => c.WeeklyHours).HasMaxLength(SlaCalendar.WeeklyMaxLength);
        builder.Property(c => c.Holidays).HasMaxLength(SlaCalendar.HolidaysMaxLength);

        builder.HasIndex(c => new { c.TenantId, c.Name }).IsUnique().HasDatabaseName("UX_SlaCalendars_Name");
    }
}

internal sealed class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        SupportTables.ConfigureCommon(builder, "SlaPolicies");
        builder.Property(p => p.Name).HasMaxLength(SlaPolicy.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(SlaPolicy.DescriptionMaxLength);
        builder.Property(p => p.TypeKey).HasMaxLength(SupportTicket.KeyMaxLength);
        builder.Property(p => p.Priority).HasMaxLength(SupportTicket.StatusMaxLength);
        builder.Property(p => p.EscalationRules).HasMaxLength(SlaPolicy.RulesMaxLength);

        builder.HasIndex(p => new { p.TenantId, p.Name }).IsUnique().HasDatabaseName("UX_SlaPolicies_Name");
        // Tenant başına en çok bir yedek (varsayılan) politika.
        builder.HasIndex(p => p.TenantId).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("UX_SlaPolicies_Default");

        builder.ToTable(t => t.HasCheckConstraint("CK_SlaPolicies_Priority", "[Priority] IS NULL OR [Priority] IN ('Low','Normal','High','Critical')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SlaPolicies_Targets",
            "([FirstResponseMinutes] IS NOT NULL OR [ResolutionMinutes] IS NOT NULL) AND ([FirstResponseMinutes] IS NULL OR [FirstResponseMinutes] > 0) AND ([ResolutionMinutes] IS NULL OR [ResolutionMinutes] > 0)"));

        builder.HasOne<TicketCategory>().WithMany().HasForeignKey(p => new { p.TenantId, p.CategoryId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SlaCalendar>().WithMany().HasForeignKey(p => new { p.TenantId, p.CalendarId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(p => new { p.TenantId, p.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SlaInstanceConfiguration : IEntityTypeConfiguration<SlaInstance>
{
    public void Configure(EntityTypeBuilder<SlaInstance> builder)
    {
        SupportTables.ConfigureCommon(builder, "SlaInstances");
        builder.Property(i => i.Metric).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(i => i.Status).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(i => i.PolicyName).HasMaxLength(SlaPolicy.NameMaxLength).IsRequired();
        builder.Property(i => i.Snapshot).HasMaxLength(SlaInstance.SnapshotMaxLength).IsRequired();
        builder.Property(i => i.CancelReason).HasMaxLength(SupportTicket.ReasonMaxLength);

        // Bir talep, bir döngüde, bir ölçüt için en çok bir örnek: tekrar çalıştırmada mükerrer örnek oluşmaz.
        builder.HasIndex(i => new { i.TenantId, i.TicketId, i.Cycle, i.Metric }).IsUnique().HasDatabaseName("UX_SlaInstances_Cycle");
        builder.HasIndex(i => new { i.TenantId, i.Status, i.DueAt });
        builder.HasIndex(i => new { i.TenantId, i.Status, i.WarningAt });

        builder.ToTable(t => t.HasCheckConstraint("CK_SlaInstances_Metric", "[Metric] IN ('FirstResponse','Resolution')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SlaInstances_Status", "[Status] IN ('Running','Paused','Completed','Cancelled')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SlaInstances_Target", "[TargetMinutes] > 0 AND [WorkedSeconds] >= 0"));

        builder.HasOne<SupportTicket>().WithMany().HasForeignKey(i => new { i.TenantId, i.TicketId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SlaPolicy>().WithMany().HasForeignKey(i => new { i.TenantId, i.PolicyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SlaPauseConfiguration : IEntityTypeConfiguration<SlaPause>
{
    public void Configure(EntityTypeBuilder<SlaPause> builder)
    {
        SupportTables.ConfigureCommon(builder, "SlaPauses");
        builder.Property(p => p.Reason).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.InstanceId, p.StartedAt });

        builder.HasOne<SlaInstance>().WithMany().HasForeignKey(p => new { p.TenantId, p.InstanceId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SlaEscalationConfiguration : IEntityTypeConfiguration<SlaEscalation>
{
    public void Configure(EntityTypeBuilder<SlaEscalation> builder)
    {
        SupportTables.ConfigureCommon(builder, "SlaEscalations");
        builder.Property(e => e.RuleId).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Metric).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(e => e.Action).HasMaxLength(SupportTicket.StatusMaxLength).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(SlaEscalation.OutcomeMaxLength);

        // Tekil eylem anahtarı: (tenant, örnek (talep + döngü + ölçüt), kural = eşik + eylem).
        builder.HasIndex(e => new { e.TenantId, e.InstanceId, e.RuleId }).IsUnique().HasDatabaseName("UX_SlaEscalations_Rule");
        builder.HasIndex(e => new { e.TenantId, e.TicketId });

        builder.HasOne<SlaInstance>().WithMany().HasForeignKey(e => new { e.TenantId, e.InstanceId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
