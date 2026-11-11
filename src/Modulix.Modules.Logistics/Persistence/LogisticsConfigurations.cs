using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Persistence;

// Lojistik tabloları "logistics" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo Row-Level
// Security politikasına eklenir (TenantIsolationSql.AddTable). Benzersizlik kuralları TenantId ile birlikte tanımlanır. Başka modülün
// kaydına (Filo aracı) veritabanı yabancı anahtarı kurulmaz: modüller birbirinin tablosuna bağlanmaz, bağ okuma sözleşmesiyle denetlenir.

internal static class LogisticsTables
{
    public const string Schema = LogisticsModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : LogisticsEntity
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

internal sealed class DriverConfiguration : IEntityTypeConfiguration<Driver>
{
    public void Configure(EntityTypeBuilder<Driver> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "Drivers");
        builder.Property(d => d.DriverType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(d => d.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(d => d.Notes).HasMaxLength(Driver.NotesMaxLength);

        builder.HasIndex(d => new { d.TenantId, d.PersonId }).IsUnique().HasDatabaseName("UX_Drivers_Person");
        builder.HasIndex(d => new { d.TenantId, d.CarrierPartyId }).HasDatabaseName("IX_Drivers_Carrier");
        builder.HasIndex(d => new { d.TenantId, d.DefaultVehicleId }).HasDatabaseName("IX_Drivers_Vehicle");
    }
}

internal sealed class CarrierTermsConfiguration : IEntityTypeConfiguration<CarrierTerms>
{
    public void Configure(EntityTypeBuilder<CarrierTerms> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "CarrierTerms");
        builder.Property(t => t.Title).HasMaxLength(CarrierTerms.TitleMaxLength).IsRequired();
        builder.Property(t => t.AgreementNo).HasMaxLength(CarrierTerms.AgreementNoMaxLength);
        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(t => t.RateNote).HasMaxLength(CarrierTerms.TextMaxLength);
        builder.Property(t => t.PerformanceNote).HasMaxLength(CarrierTerms.TextMaxLength);
        builder.Property(t => t.IsActive).HasDefaultValue(true);

        builder.HasIndex(t => new { t.TenantId, t.CarrierPartyId, t.ValidFrom }).HasDatabaseName("IX_CarrierTerms_Carrier");
        builder.HasIndex(t => new { t.TenantId, t.CarrierPartyId, t.AgreementNo }).IsUnique()
            .HasFilter("[AgreementNo] IS NOT NULL").HasDatabaseName("UX_CarrierTerms_AgreementNo");
    }
}
