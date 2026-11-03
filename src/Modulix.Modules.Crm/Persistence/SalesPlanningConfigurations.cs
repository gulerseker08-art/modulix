using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customization;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

internal sealed class SalesTargetConfiguration : IEntityTypeConfiguration<SalesTarget>
{
    public void Configure(EntityTypeBuilder<SalesTarget> builder)
    {
        CrmTables.ConfigureCommon(builder, "SalesTargets");
        builder.Ignore(t => t.PendingReason);
        builder.Property(t => t.Metric).HasMaxLength(20).IsRequired();
        builder.Property(t => t.ScopeType).HasMaxLength(10).IsRequired();
        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(t => t.PeriodStart).HasColumnType("date");
        builder.Property(t => t.PeriodEnd).HasColumnType("date");
        builder.Property(t => t.Value).HasPrecision(18, 2);
        builder.Property(t => t.Note).HasMaxLength(SalesTarget.NoteMaxLength);

        builder.HasIndex(t => new { t.TenantId, t.Metric, t.ScopeType, t.ScopeUserId, t.Currency, t.PeriodStart }).IsUnique().HasDatabaseName("UX_SalesTargets_Slot");
        builder.HasIndex(t => new { t.TenantId, t.PeriodStart, t.PeriodEnd });
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesTargets_Metric", "[Metric] IN ('WonAmount','WonCount')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesTargets_Scope",
            "([ScopeType] = 'Tenant' AND [ScopeUserId] IS NULL) OR ([ScopeType] = 'User' AND [ScopeUserId] IS NOT NULL)"));
        // Tutar hedefi para birimi ister; adet hedefinin para birimi olmaz.
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesTargets_Currency",
            "([Metric] = 'WonAmount' AND [Currency] IS NOT NULL) OR ([Metric] = 'WonCount' AND [Currency] IS NULL)"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesTargets_Period", "[PeriodEnd] >= [PeriodStart]"));
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesTargets_Value", "[Value] > 0"));
    }
}

internal sealed class SalesTargetRevisionConfiguration : IEntityTypeConfiguration<SalesTargetRevision>
{
    public void Configure(EntityTypeBuilder<SalesTargetRevision> builder)
    {
        CrmTables.ConfigureCommon(builder, "SalesTargetRevisions");
        builder.Property(r => r.OldValue).HasPrecision(18, 2);
        builder.Property(r => r.NewValue).HasPrecision(18, 2);
        builder.Property(r => r.Reason).HasMaxLength(ForecastSubmission.NoteMaxLength).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.TargetId, r.RevisionNo }).IsUnique();
        builder.HasOne<SalesTarget>().WithMany().HasForeignKey(r => new { r.TenantId, r.TargetId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ForecastSubmissionConfiguration : IEntityTypeConfiguration<ForecastSubmission>
{
    public void Configure(EntityTypeBuilder<ForecastSubmission> builder)
    {
        CrmTables.ConfigureCommon(builder, "ForecastSubmissions");
        builder.Property(s => s.PeriodKind).HasMaxLength(10).IsRequired();
        builder.Property(s => s.PeriodStart).HasColumnType("date");
        builder.Property(s => s.PeriodEnd).HasColumnType("date");
        builder.Property(s => s.ScopeType).HasMaxLength(10).IsRequired();
        builder.Property(s => s.ReportingCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(s => s.Status).HasMaxLength(12).IsRequired();
        builder.Property(s => s.Note).HasMaxLength(ForecastSubmission.NoteMaxLength);
        builder.Property(s => s.ReopenReason).HasMaxLength(ForecastSubmission.NoteMaxLength);
        builder.Property(s => s.ReturnReason).HasMaxLength(ForecastSubmission.NoteMaxLength);
        builder.Property(s => s.RealizedAtLock).HasPrecision(18, 2);

        // Aynı dönem, kapsam ve para biriminde sürüm numarası benzersiz; kilitli olmayan sürüm en fazla bir tane (eşzamanlı çift taslağı DB engeller).
        builder.HasIndex(s => new { s.TenantId, s.PeriodStart, s.PeriodEnd, s.ScopeType, s.ScopeUserId, s.ReportingCurrency, s.VersionNo })
            .IsUnique().HasDatabaseName("UX_ForecastSubmissions_Version");
        builder.HasIndex(s => new { s.TenantId, s.PeriodStart, s.PeriodEnd, s.ScopeType, s.ScopeUserId, s.ReportingCurrency })
            .IsUnique().HasFilter("[Status] <> 'Locked'").HasDatabaseName("UX_ForecastSubmissions_Open");
        builder.HasIndex(s => new { s.TenantId, s.Status });
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastSubmissions_Status", "[Status] IN ('Draft','Submitted','Locked')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastSubmissions_Scope",
            "([ScopeType] = 'Tenant' AND [ScopeUserId] IS NULL) OR ([ScopeType] = 'User' AND [ScopeUserId] IS NOT NULL)"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastSubmissions_Period", "[PeriodEnd] >= [PeriodStart]"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastSubmissions_Locked", "[Status] <> 'Locked' OR [LockedAt] IS NOT NULL"));
    }
}

internal sealed class ForecastItemConfiguration : IEntityTypeConfiguration<ForecastItem>
{
    public void Configure(EntityTypeBuilder<ForecastItem> builder)
    {
        CrmTables.ConfigureCommon(builder, "ForecastItems");
        builder.Property(i => i.OpportunityNumber).HasMaxLength(ForecastItem.NumberMaxLength).IsRequired();
        builder.Property(i => i.Title).HasMaxLength(ForecastItem.TitleMaxLength).IsRequired();
        builder.Property(i => i.Stage).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(i => i.SourceAmount).HasPrecision(18, 2);
        builder.Property(i => i.SourceCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.ExpectedCloseDate).HasColumnType("date");
        builder.Property(i => i.RateUsed).HasPrecision(28, 10);
        builder.Property(i => i.RateDate).HasColumnType("date");
        builder.Property(i => i.BaseAmount).HasPrecision(18, 2);
        builder.Property(i => i.BaseCategory).HasMaxLength(10).IsRequired();
        builder.Property(i => i.Category).HasMaxLength(10).IsRequired();
        builder.Property(i => i.AdjustmentAmount).HasPrecision(18, 2);

        builder.HasIndex(i => new { i.TenantId, i.SubmissionId, i.OpportunityId }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastItems_Category", "[Category] IN ('Omitted','Pipeline','BestCase','Commit')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastItems_BaseCategory", "[BaseCategory] IN ('Omitted','Pipeline','BestCase','Commit')"));
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastItems_Probability",
            "([Probability] IS NULL OR [Probability] BETWEEN 0 AND 100) AND ([ProbabilityOverride] IS NULL OR [ProbabilityOverride] BETWEEN 0 AND 100)"));
        builder.HasOne<ForecastSubmission>().WithMany().HasForeignKey(i => new { i.TenantId, i.SubmissionId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ForecastAdjustmentConfiguration : IEntityTypeConfiguration<ForecastAdjustment>
{
    public void Configure(EntityTypeBuilder<ForecastAdjustment> builder)
    {
        CrmTables.ConfigureCommon(builder, "ForecastAdjustments");
        builder.Property(a => a.Kind).HasMaxLength(12).IsRequired();
        builder.Property(a => a.FromValue).HasMaxLength(ForecastAdjustment.ValueMaxLength);
        builder.Property(a => a.ToValue).HasMaxLength(ForecastAdjustment.ValueMaxLength);
        builder.Property(a => a.Reason).HasMaxLength(ForecastSubmission.NoteMaxLength).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.SubmissionId, a.OpportunityId });
        builder.ToTable(t => t.HasCheckConstraint("CK_ForecastAdjustments_Kind", "[Kind] IN ('Category','Amount','Probability')"));
        builder.HasOne<ForecastSubmission>().WithMany().HasForeignKey(a => new { a.TenantId, a.SubmissionId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
