using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Domain;

namespace Modulix.Modules.Contracts.Persistence;

// Sözleşme tabloları "contracts" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo
// Row-Level Security politikasına eklenir (TenantIsolationSql.AddTable). Kayıtlar arası bağlar (TenantId, Id) üzerinden kurulur:
// başka tenant'ın kaydına veritabanı düzeyinde de bağlanılamaz. Ortak kayıtlara (core.party / core.person) bağ da bileşiktir.

internal static class ContractsTables
{
    public const string Schema = ContractsModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : ContractsEntity
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

internal sealed class ContractConfiguration : IEntityTypeConfiguration<Contract>
{
    public void Configure(EntityTypeBuilder<Contract> builder)
    {
        ContractsTables.ConfigureCommon(builder, "Contracts");
        builder.Property(c => c.Number).HasMaxLength(Contract.NumberMaxLength).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(Contract.TitleMaxLength).IsRequired();
        builder.Property(c => c.Type).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(Contract.TextMaxLength);
        builder.Property(c => c.StartDate).HasColumnType("date");
        builder.Property(c => c.EndDate).HasColumnType("date");
        builder.Property(c => c.NoticeDeadline).HasColumnType("date");
        builder.Property(c => c.Currency).HasMaxLength(Contract.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(c => c.ContractValue).HasPrecision(18, 2);
        builder.Property(c => c.PaymentTerms).HasMaxLength(Contract.TextMaxLength);
        builder.Property(c => c.RenewalMode).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.Confidentiality).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.RiskLevel).HasMaxLength(Contract.StatusMaxLength).HasDefaultValue(Lifecycle.RiskLevel.Low).IsRequired();
        builder.Property(c => c.PreparationStatus).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.ApprovalStatus).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.SignatureStatus).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.EffectiveStatus).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.StatusReason).HasMaxLength(Contract.ReasonMaxLength);
        builder.Property(c => c.NetTotal).HasPrecision(18, 2);
        builder.Property(c => c.TaxTotal).HasPrecision(18, 2);
        builder.Property(c => c.GrossTotal).HasPrecision(18, 2);
        builder.Property(c => c.RecurringSummary).HasMaxLength(Contract.SummaryMaxLength);

        builder.HasIndex(c => new { c.TenantId, c.Number }).IsUnique().HasDatabaseName("UX_Contracts_Number");
        builder.HasIndex(c => new { c.TenantId, c.PartyId });
        builder.HasIndex(c => new { c.TenantId, c.OwnerId, c.EffectiveStatus });
        builder.Property(c => c.SourceQuoteNumber).HasMaxLength(Contract.NumberMaxLength);
        builder.Property(c => c.SourceQuoteSha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false);
        builder.HasIndex(c => new { c.TenantId, c.SourceQuoteId });
        builder.HasIndex(c => new { c.TenantId, c.SourceOpportunityId });
        // Tarih ve durum sorguları (bitişi yaklaşan, bildirim süresi yaklaşan, imza bekleyen).
        builder.HasIndex(c => new { c.TenantId, c.EffectiveStatus, c.EndDate });
        builder.HasIndex(c => new { c.TenantId, c.NoticeDeadline });
        builder.HasIndex(c => new { c.TenantId, c.SignatureStatus });
        builder.ToTable(t => t.HasCheckConstraint("CK_Contracts_Version", "[CurrentVersionNo] >= 1"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Contracts_Dates", "[EndDate] IS NULL OR [StartDate] IS NULL OR [EndDate] >= [StartDate]"));

        // Ortak kayıtlar (ADR-0006): müşteri taraf ve kişi aynı tenant'ta olmalıdır.
        builder.HasOne<Party>().WithMany().HasForeignKey(c => new { c.TenantId, c.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Person>().WithMany().HasForeignKey(c => new { c.TenantId, c.PersonId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contract>().WithMany().HasForeignKey(c => new { c.TenantId, c.PreviousContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractPartyConfiguration : IEntityTypeConfiguration<ContractParty>
{
    public void Configure(EntityTypeBuilder<ContractParty> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractParties");
        builder.Property(p => p.Role).HasMaxLength(ContractParty.RoleMaxLength).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(ContractParty.NotesMaxLength);
        builder.HasIndex(p => new { p.TenantId, p.ContractId });
        builder.HasIndex(p => new { p.TenantId, p.PartyId });
        builder.HasIndex(p => new { p.TenantId, p.PersonId });

        builder.HasOne<Contract>().WithMany().HasForeignKey(p => new { p.TenantId, p.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Party>().WithMany().HasForeignKey(p => new { p.TenantId, p.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Person>().WithMany().HasForeignKey(p => new { p.TenantId, p.PersonId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractLineConfiguration : IEntityTypeConfiguration<ContractLine>
{
    public void Configure(EntityTypeBuilder<ContractLine> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractLines");
        builder.Property(l => l.Kind).HasMaxLength(ContractLine.KindMaxLength).IsRequired();
        builder.Property(l => l.ProductCode).HasMaxLength(ContractLine.CodeMaxLength);
        builder.Property(l => l.Name).HasMaxLength(ContractLine.NameMaxLength).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(ContractLine.TextMaxLength);
        builder.Property(l => l.UnitName).HasMaxLength(ContractLine.UnitMaxLength);
        builder.Property(l => l.BillingPeriod).HasMaxLength(ContractLine.PeriodMaxLength);
        builder.Property(l => l.Quantity).HasPrecision(18, 6);
        builder.Property(l => l.UnitPrice).HasPrecision(18, 6);
        builder.Property(l => l.DiscountPercent).HasPrecision(9, 4);
        builder.Property(l => l.TaxRatePercent).HasPrecision(9, 4);
        builder.Property(l => l.ListAmount).HasPrecision(18, 2);
        builder.Property(l => l.LineDiscount).HasPrecision(18, 2);
        builder.Property(l => l.Net).HasPrecision(18, 2);
        builder.Property(l => l.Tax).HasPrecision(18, 2);
        builder.Property(l => l.Gross).HasPrecision(18, 2);
        builder.HasIndex(l => new { l.TenantId, l.ContractId, l.Position });
        builder.HasIndex(l => new { l.TenantId, l.ContractId, l.StableKey }).IsUnique().HasDatabaseName("UX_ContractLines_StableKey");
        // Teklif kalemi aynı anda ikinci bir sözleşmeye giremez (mükerrer devir; yarışta veritabanı ikincisini reddeder). İptalde SourceQuoteId boşaltılır.
        builder.HasIndex(l => new { l.TenantId, l.SourceQuoteId, l.SourceQuoteLineKey }).IsUnique().HasDatabaseName("UX_ContractLines_SourceQuoteLine")
            .HasFilter("[SourceQuoteId] IS NOT NULL AND [SourceQuoteLineKey] IS NOT NULL");

        builder.HasOne<Contract>().WithMany().HasForeignKey(l => new { l.TenantId, l.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContractEventConfiguration : IEntityTypeConfiguration<ContractEvent>
{
    public void Configure(EntityTypeBuilder<ContractEvent> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractEvents");
        builder.Property(e => e.Kind).HasMaxLength(ContractEvent.KindMaxLength).IsRequired();
        // Geçiş öncesi/sonrası özet: "hazırlık/onay/imza/yürürlük" dört durumu birlikte taşır.
        builder.Property(e => e.FromStatus).HasMaxLength(ContractEvent.StatusSummaryMaxLength);
        builder.Property(e => e.ToStatus).HasMaxLength(ContractEvent.StatusSummaryMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.ContractId, e.CreatedAt });

        builder.HasOne<Contract>().WithMany().HasForeignKey(e => new { e.TenantId, e.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContractVersionConfiguration : IEntityTypeConfiguration<ContractVersion>
{
    public void Configure(EntityTypeBuilder<ContractVersion> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractVersions");
        builder.Property(v => v.Kind).HasMaxLength(ContractVersion.KindMaxLength).IsRequired();
        builder.Property(v => v.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(v => v.Snapshot).IsRequired();
        builder.Property(v => v.Sha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(v => v.Reason).HasMaxLength(Contract.ReasonMaxLength);
        builder.Property(v => v.ApprovalPlan);
        builder.HasIndex(v => new { v.TenantId, v.ContractId, v.VersionNo }).IsUnique().HasDatabaseName("UX_ContractVersions_Contract_VersionNo");

        builder.HasOne<Contract>().WithMany().HasForeignKey(v => new { v.TenantId, v.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContractTemplateConfiguration : IEntityTypeConfiguration<ContractTemplate>
{
    public void Configure(EntityTypeBuilder<ContractTemplate> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractTemplates");
        builder.Property(t => t.Name).HasMaxLength(ContractTemplate.NameMaxLength).IsRequired();
        builder.Property(t => t.Language).HasMaxLength(2).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(t => t.ContractType).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(t => t.UsageScope).HasMaxLength(ContractTemplate.ScopeMaxLength);
        builder.Property(t => t.Description).HasMaxLength(ContractTemplate.DescriptionMaxLength);
        builder.Property(t => t.Body).IsRequired();
        builder.Property(t => t.BodySha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(t => t.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.HasIndex(t => new { t.TenantId, t.Name, t.Language }).IsUnique().HasDatabaseName("UX_ContractTemplates_NameLanguage");
        builder.HasIndex(t => new { t.TenantId, t.Status });
    }
}

internal sealed class ContractTemplateVersionConfiguration : IEntityTypeConfiguration<ContractTemplateVersion>
{
    public void Configure(EntityTypeBuilder<ContractTemplateVersion> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractTemplateVersions");
        builder.Property(v => v.Name).HasMaxLength(ContractTemplate.NameMaxLength).IsRequired();
        builder.Property(v => v.Body).IsRequired();
        builder.Property(v => v.Sha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(v => v.Notes).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(v => new { v.TenantId, v.TemplateId, v.VersionNo }).IsUnique().HasDatabaseName("UX_ContractTemplateVersions_TemplateVersion");

        builder.HasOne<ContractTemplate>().WithMany().HasForeignKey(v => new { v.TenantId, v.TemplateId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractDocumentConfiguration : IEntityTypeConfiguration<ContractDocument>
{
    public void Configure(EntityTypeBuilder<ContractDocument> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractDocuments");
        builder.Property(d => d.TemplateName).HasMaxLength(ContractTemplate.NameMaxLength).IsRequired();
        builder.Property(d => d.FileName).HasMaxLength(ContractDocument.FileNameMaxLength).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.Snapshot).IsRequired();
        builder.Property(d => d.Content).IsRequired();
        builder.HasIndex(d => new { d.TenantId, d.ContractId, d.CreatedAt });
        builder.HasIndex(d => new { d.TenantId, d.TemplateId });

        builder.HasOne<Contract>().WithMany().HasForeignKey(d => new { d.TenantId, d.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        // Şablon silinemez (belgeler ona bağlı kalır); arşivlenir.
        builder.HasOne<ContractTemplate>().WithMany().HasForeignKey(d => new { d.TenantId, d.TemplateId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractApprovalRuleConfiguration : IEntityTypeConfiguration<ContractApprovalRule>
{
    public void Configure(EntityTypeBuilder<ContractApprovalRule> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractApprovalRules");
        builder.Property(r => r.Name).HasMaxLength(ContractApprovalRule.NameMaxLength).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(ContractApprovalRule.DescriptionMaxLength);
        builder.Property(r => r.ContractType).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(r => r.Currency).HasMaxLength(Contract.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.MinGrossTotal).HasPrecision(18, 2);
        builder.Property(r => r.MinDiscountPercent).HasPrecision(5, 2);
        builder.Property(r => r.MinRiskLevel).HasMaxLength(Contract.StatusMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.IsActive, r.Step, r.Priority });
        builder.ToTable(t => t.HasCheckConstraint("CK_ContractApprovalRules_Step", "[Step] BETWEEN 1 AND 9"));
    }
}

internal sealed class ContractApprovalDelegationConfiguration : IEntityTypeConfiguration<ContractApprovalDelegation>
{
    public void Configure(EntityTypeBuilder<ContractApprovalDelegation> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractApprovalDelegations");
        builder.Property(d => d.Note).HasMaxLength(ContractApprovalDelegation.NoteMaxLength);
        builder.HasIndex(d => new { d.TenantId, d.FromUserId, d.IsActive });
        builder.HasIndex(d => new { d.TenantId, d.ToUserId, d.IsActive });
    }
}

internal sealed class ContractApprovalDecisionConfiguration : IEntityTypeConfiguration<ContractApprovalDecision>
{
    public void Configure(EntityTypeBuilder<ContractApprovalDecision> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractApprovalDecisions");
        builder.Property(d => d.OnBehalfOf).HasMaxLength(ContractApprovalDecision.OnBehalfMaxLength).IsUnicode(false).IsRequired();
        builder.Property(d => d.Decision).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(d => d.Reason).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(d => new { d.TenantId, d.ContractId, d.VersionNo, d.Round, d.CreatedAt });

        builder.HasOne<Contract>().WithMany().HasForeignKey(d => new { d.TenantId, d.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContractSignatureRequestConfiguration : IEntityTypeConfiguration<ContractSignatureRequest>
{
    public void Configure(EntityTypeBuilder<ContractSignatureRequest> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractSignatureRequests");
        builder.Property(r => r.DocumentSha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(r => r.Method).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(r => r.Provider).HasMaxLength(40);
        builder.Property(r => r.ProviderReference).HasMaxLength(ContractSignatureRequest.ReferenceMaxLength).IsUnicode(false);
        builder.Property(r => r.Mode).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(r => r.CloseReason).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.ContractId, r.VersionNo, r.CreatedAt });
        builder.HasIndex(r => new { r.TenantId, r.Status, r.ExpiresAt });
        // Aynı sürümde aynı anda yalnızca bir açık istek olabilir (eşzamanlı başlatmada ikincisi reddedilir).
        builder.HasIndex(r => new { r.TenantId, r.ContractId, r.VersionNo }).IsUnique().HasDatabaseName("UX_ContractSignatureRequests_OpenPerVersion")
            .HasFilter("[Status] IN (N'Pending', N'PartiallySigned')");
        // Sağlayıcı referansı tenant içinde tekildir (olaylar buna bağlanır).
        builder.HasIndex(r => new { r.TenantId, r.ProviderReference }).IsUnique().HasDatabaseName("UX_ContractSignatureRequests_ProviderReference")
            .HasFilter("[ProviderReference] IS NOT NULL");

        builder.HasOne<Contract>().WithMany().HasForeignKey(r => new { r.TenantId, r.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractSignatoryConfiguration : IEntityTypeConfiguration<ContractSignatory>
{
    public void Configure(EntityTypeBuilder<ContractSignatory> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractSignatories");
        builder.Property(s => s.Kind).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(ContractSignatory.NameMaxLength).IsRequired();
        builder.Property(s => s.Email).HasMaxLength(ContractSignatory.EmailMaxLength);
        builder.Property(s => s.Capacity).HasMaxLength(ContractSignatory.CapacityMaxLength);
        builder.Property(s => s.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(s => s.Assurance).HasMaxLength(40);
        builder.Property(s => s.DeclineReason).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(s => new { s.TenantId, s.RequestId, s.Position }).IsUnique().HasDatabaseName("UX_ContractSignatories_RequestPosition");
        builder.HasIndex(s => new { s.TenantId, s.ContractId });
        builder.HasIndex(s => new { s.TenantId, s.UserId, s.Status });

        builder.HasOne<ContractSignatureRequest>().WithMany().HasForeignKey(s => new { s.TenantId, s.RequestId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractSignatureEventConfiguration : IEntityTypeConfiguration<ContractSignatureEvent>
{
    public void Configure(EntityTypeBuilder<ContractSignatureEvent> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractSignatureEvents");
        builder.Property(e => e.Source).HasMaxLength(20).IsRequired();
        builder.Property(e => e.EventType).HasMaxLength(30).IsRequired();
        builder.Property(e => e.ProviderEventId).HasMaxLength(ContractSignatureEvent.EventIdMaxLength).IsUnicode(false);
        builder.Property(e => e.Outcome).HasMaxLength(20).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(ContractSignatureEvent.NoteMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.RequestId, e.CreatedAt });
        // Sağlayıcı olayı idempotency: aynı istekte aynı olay kimliği ikinci kez yazılamaz.
        builder.HasIndex(e => new { e.TenantId, e.RequestId, e.ProviderEventId }).IsUnique().HasDatabaseName("UX_ContractSignatureEvents_ProviderEvent")
            .HasFilter("[ProviderEventId] IS NOT NULL");

        builder.HasOne<ContractSignatureRequest>().WithMany().HasForeignKey(e => new { e.TenantId, e.RequestId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractSignatureFileConfiguration : IEntityTypeConfiguration<ContractSignatureFile>
{
    public void Configure(EntityTypeBuilder<ContractSignatureFile> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractSignatureFiles");
        builder.Property(f => f.FileName).HasMaxLength(ContractSignatureFile.FileNameMaxLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(f => f.Content).IsRequired();
        builder.HasIndex(f => new { f.TenantId, f.ContractId });
        builder.HasIndex(f => new { f.TenantId, f.SignatoryId });
    }
}

internal sealed class ContractCommitmentConfiguration : IEntityTypeConfiguration<ContractCommitment>
{
    public void Configure(EntityTypeBuilder<ContractCommitment> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractCommitments");
        builder.Property(c => c.Title).HasMaxLength(ContractCommitment.TitleMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(ContractCommitment.TextMaxLength);
        builder.Property(c => c.Category).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(c => c.Obligor).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.Kind).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(c => c.Recurrence).HasMaxLength(Contract.StatusMaxLength);
        builder.Property(c => c.StartDate).HasColumnType("date");
        builder.Property(c => c.DueDate).HasColumnType("date");
        builder.Property(c => c.RecurrenceEndDate).HasColumnType("date");
        builder.Property(c => c.MetricName).HasMaxLength(ContractCommitment.MetricMaxLength);
        builder.Property(c => c.Unit).HasMaxLength(ContractCommitment.UnitMaxLength);
        builder.Property(c => c.Direction).HasMaxLength(Contract.StatusMaxLength);
        builder.Property(c => c.TargetValue).HasPrecision(18, 6);
        builder.Property(c => c.Tolerance).HasPrecision(18, 6);
        builder.Property(c => c.ConsequenceNote).HasMaxLength(ContractCommitment.TextMaxLength);
        builder.Property(c => c.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.ContractId, c.Status });
        builder.HasIndex(c => new { c.TenantId, c.ResponsibleUserId, c.Status });
        builder.ToTable(t => t.HasCheckConstraint("CK_ContractCommitments_Grace", "[GraceDays] BETWEEN 0 AND 365 AND [EscalateAfterDays] BETWEEN 0 AND 365"));

        builder.HasOne<Contract>().WithMany().HasForeignKey(c => new { c.TenantId, c.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ContractCommitment>().WithMany().HasForeignKey(c => new { c.TenantId, c.DependsOnCommitmentId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractCommitmentOccurrenceConfiguration : IEntityTypeConfiguration<ContractCommitmentOccurrence>
{
    public void Configure(EntityTypeBuilder<ContractCommitmentOccurrence> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractCommitmentOccurrences");
        builder.Property(o => o.DueDate).HasColumnType("date");
        builder.Property(o => o.CompletedOn).HasColumnType("date");
        builder.Property(o => o.Status).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(o => o.ActualValue).HasPrecision(18, 6);
        builder.Property(o => o.Note).HasMaxLength(ContractCommitmentOccurrence.NoteMaxLength);
        builder.Property(o => o.BreachReason).HasMaxLength(Contract.ReasonMaxLength);
        builder.Property(o => o.FollowUpStatus).HasMaxLength(Contract.StatusMaxLength).IsRequired();
        builder.Property(o => o.WaiveReason).HasMaxLength(Contract.ReasonMaxLength);
        // Aynı taahhüt ve dönem için ikinci satır üretilemez (zamanlanmış işin tekrar çalışması mükerrer yaratmaz).
        builder.HasIndex(o => new { o.TenantId, o.CommitmentId, o.Sequence }).IsUnique().HasDatabaseName("UX_ContractCommitmentOccurrences_Sequence");
        builder.HasIndex(o => new { o.TenantId, o.ContractId, o.Status, o.DueDate });
        builder.HasIndex(o => new { o.TenantId, o.Status, o.DueDate });

        builder.HasOne<ContractCommitment>().WithMany().HasForeignKey(o => new { o.TenantId, o.CommitmentId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractCommitmentFileConfiguration : IEntityTypeConfiguration<ContractCommitmentFile>
{
    public void Configure(EntityTypeBuilder<ContractCommitmentFile> builder)
    {
        ContractsTables.ConfigureCommon(builder, "ContractCommitmentFiles");
        builder.Property(f => f.FileName).HasMaxLength(ContractCommitmentFile.FileNameMaxLength).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(ContractVersion.HashLength).IsUnicode(false).IsRequired();
        builder.Property(f => f.Content).IsRequired();
        builder.HasIndex(f => new { f.TenantId, f.OccurrenceId });
    }
}
