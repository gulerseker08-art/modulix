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

internal static class CargoTables
{
    public static void Configure<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : CargoEntity
    {
        LogisticsTables.ConfigureCommon(builder, table);
        builder.Property(e => e.TransportModeKey).HasMaxLength(50).IsRequired();
        builder.Property(e => e.PickupCity).HasMaxLength(CargoEntity.CityMaxLength).IsRequired();
        builder.Property(e => e.PickupCountry).HasMaxLength(CargoEntity.CountryLength).IsUnicode(false).IsFixedLength().IsRequired().HasDefaultValue(CargoEntity.DefaultCountry);
        builder.Property(e => e.DeliveryCountry).HasMaxLength(CargoEntity.CountryLength).IsUnicode(false).IsFixedLength().IsRequired().HasDefaultValue(CargoEntity.DefaultCountry);
        builder.Property(e => e.PickupAddress).HasMaxLength(CargoEntity.AddressMaxLength);
        builder.Property(e => e.DeliveryCity).HasMaxLength(CargoEntity.CityMaxLength).IsRequired();
        builder.Property(e => e.DeliveryAddress).HasMaxLength(CargoEntity.AddressMaxLength);
        builder.Property(e => e.CargoDescription).HasMaxLength(CargoEntity.TextMaxLength).IsRequired();
        builder.Property(e => e.WeightKg).HasPrecision(12, 2);
        builder.Property(e => e.VolumeM3).HasPrecision(10, 2);
        builder.Property(e => e.LoadingMeters).HasPrecision(6, 2);
        builder.Property(e => e.Notes).HasMaxLength(CargoEntity.TextMaxLength);
        builder.Property(e => e.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(e => e.StatusReason).HasMaxLength(CargoEntity.ReasonMaxLength);

        builder.HasIndex(e => new { e.TenantId, e.Status, e.PickupDate }).HasDatabaseName($"IX_{table}_Status");
        builder.HasIndex(e => new { e.TenantId, e.CustomerPartyId }).HasDatabaseName($"IX_{table}_Customer");
    }
}

internal sealed class TransportRequestConfiguration : IEntityTypeConfiguration<TransportRequest>
{
    public void Configure(EntityTypeBuilder<TransportRequest> builder)
    {
        CargoTables.Configure(builder, "TransportRequests");
        builder.Property(r => r.Source).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(r => r.ExternalRef).HasMaxLength(TransportRequest.ExternalRefMaxLength);

        // Aynı kaynaktan aynı referansla iki talep açılmaz (API yeniden denemesi çift kayıt üretmez, ADR-0038 tablo).
        builder.HasIndex(r => new { r.TenantId, r.Source, r.ExternalRef }).IsUnique()
            .HasFilter("[ExternalRef] IS NOT NULL").HasDatabaseName("UX_TransportRequests_ExternalRef");
    }
}

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        CargoTables.Configure(builder, "Shipments");
        builder.Property(s => s.FreightAmount).HasPrecision(18, 2);
        builder.Property(s => s.FreightCurrency).HasMaxLength(LogisticsEntity.CurrencyLength).IsFixedLength().IsUnicode(false);

        // Her talepten en çok bir sevkiyat; talebe veritabanı yabancı anahtarı (aynı modül) bileşik anahtarla kurulur.
        builder.HasIndex(s => new { s.TenantId, s.RequestId }).IsUnique().HasDatabaseName("UX_Shipments_Request");
        builder.HasOne<TransportRequest>().WithMany().HasForeignKey(s => new { s.TenantId, s.RequestId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);

        // Atandığı sefer (aynı modül, bileşik yabancı anahtar).
        builder.HasIndex(s => new { s.TenantId, s.TripId }).HasDatabaseName("IX_Shipments_Trip");
        builder.HasOne<Trip>().WithMany().HasForeignKey(s => new { s.TenantId, s.TripId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TripConfiguration : IEntityTypeConfiguration<Trip>
{
    public void Configure(EntityTypeBuilder<Trip> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "Trips");
        builder.Property(t => t.Number).HasMaxLength(Trip.NumberMaxLength).IsUnicode(false).IsRequired();
        builder.Property(t => t.Notes).HasMaxLength(Trip.TextMaxLength);
        builder.Property(t => t.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(t => t.StatusReason).HasMaxLength(Trip.ReasonMaxLength);
        builder.Property(t => t.SettlementAmount).HasPrecision(18, 2);
        builder.Property(t => t.SettlementCurrency).HasMaxLength(LogisticsEntity.CurrencyLength).IsFixedLength().IsUnicode(false);

        builder.HasIndex(t => new { t.TenantId, t.Number }).IsUnique().HasDatabaseName("UX_Trips_Number");
        builder.HasIndex(t => new { t.TenantId, t.Status, t.PlannedStart }).HasDatabaseName("IX_Trips_Status");
        builder.HasIndex(t => new { t.TenantId, t.VehicleId }).HasDatabaseName("IX_Trips_Vehicle");
        builder.HasIndex(t => new { t.TenantId, t.DriverId }).HasDatabaseName("IX_Trips_Driver");
        builder.HasOne<Driver>().WithMany().HasForeignKey(t => new { t.TenantId, t.DriverId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Driver>().WithMany().HasForeignKey(t => new { t.TenantId, t.CoDriverId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TripStopConfiguration : IEntityTypeConfiguration<TripStop>
{
    public void Configure(EntityTypeBuilder<TripStop> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "TripStops");
        builder.Property(s => s.StopType).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(s => s.City).HasMaxLength(CargoEntity.CityMaxLength).IsRequired();
        builder.Property(s => s.Address).HasMaxLength(CargoEntity.AddressMaxLength);
        builder.Property(s => s.Notes).HasMaxLength(CargoEntity.TextMaxLength);

        // Sıra sefer içinde tekildir (ADR-0038 bölüm 3).
        builder.HasIndex(s => new { s.TenantId, s.TripId, s.Sequence }).IsUnique().HasDatabaseName("UX_TripStops_Sequence");
        builder.HasIndex(s => new { s.TenantId, s.ShipmentId }).HasDatabaseName("IX_TripStops_Shipment");
        builder.HasOne<Trip>().WithMany().HasForeignKey(s => new { s.TenantId, s.TripId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Shipment>().WithMany().HasForeignKey(s => new { s.TenantId, s.ShipmentId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeliveryProofConfiguration : IEntityTypeConfiguration<DeliveryProof>
{
    public void Configure(EntityTypeBuilder<DeliveryProof> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "DeliveryProofs");
        builder.Property(p => p.Outcome).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(p => p.ReceivedBy).HasMaxLength(DeliveryProof.ReceivedByMaxLength);
        builder.Property(p => p.FailureReason).HasMaxLength(DeliveryProof.TextMaxLength);
        builder.Property(p => p.Notes).HasMaxLength(DeliveryProof.TextMaxLength);

        // Bir sevkiyatın birden çok kanıtı olabilir (düzeltme yeni kanıtla yapılır); sevkiyata aynı modül içinde bileşik yabancı anahtar.
        builder.HasIndex(p => new { p.TenantId, p.ShipmentId, p.ReceivedAt }).HasDatabaseName("IX_DeliveryProofs_Shipment");
        builder.HasOne<Shipment>().WithMany().HasForeignKey(p => new { p.TenantId, p.ShipmentId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TripExpenseConfiguration : IEntityTypeConfiguration<TripExpense>
{
    public void Configure(EntityTypeBuilder<TripExpense> builder)
    {
        LogisticsTables.ConfigureCommon(builder, "TripExpenses");
        builder.Property(e => e.KindKey).HasMaxLength(50).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(TripExpense.DescriptionMaxLength).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.Currency).HasMaxLength(LogisticsEntity.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(e => e.VatAmount).HasPrecision(18, 2);
        builder.Property(e => e.DocumentNo).HasMaxLength(TripExpense.DocumentNoMaxLength);
        builder.Property(e => e.Liters).HasPrecision(12, 3);
        builder.Property(e => e.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(e => e.StatusReason).HasMaxLength(TripExpense.ReasonMaxLength);

        // Seferin masrafları tarih sırasıyla; onay bekleyenler durum dizininden. Sefere aynı modül içinde bileşik yabancı anahtar.
        builder.HasIndex(e => new { e.TenantId, e.TripId, e.ExpenseDate }).HasDatabaseName("IX_TripExpenses_Trip");
        builder.HasIndex(e => new { e.TenantId, e.Status, e.ExpenseDate }).HasDatabaseName("IX_TripExpenses_Status");
        builder.HasOne<Trip>().WithMany().HasForeignKey(e => new { e.TenantId, e.TripId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
