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
        builder.Property(e => e.FromStatus).HasMaxLength(Contract.StatusMaxLength);
        builder.Property(e => e.ToStatus).HasMaxLength(Contract.StatusMaxLength);
        builder.Property(e => e.Reason).HasMaxLength(Contract.ReasonMaxLength);
        builder.HasIndex(e => new { e.TenantId, e.ContractId, e.CreatedAt });

        builder.HasOne<Contract>().WithMany().HasForeignKey(e => new { e.TenantId, e.ContractId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}
