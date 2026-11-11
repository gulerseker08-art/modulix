using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Fleet.Domain;

namespace Modulix.Modules.Fleet.Persistence;

// Filo tabloları "fleet" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo Row-Level
// Security politikasına eklenir (TenantIsolationSql.AddTable). Benzersizlik kuralları TenantId ile birlikte tanımlanır.

internal static class FleetTables
{
    public const string Schema = FleetModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : FleetEntity
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

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        FleetTables.ConfigureCommon(builder, "Vehicles");
        builder.Property(v => v.Plate).HasMaxLength(Vehicle.PlateMaxLength).IsRequired();
        builder.Property(v => v.KindKey).HasMaxLength(100).IsRequired();
        builder.Property(v => v.Ownership).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(v => v.Brand).HasMaxLength(Vehicle.BrandMaxLength);
        builder.Property(v => v.Model).HasMaxLength(Vehicle.ModelMaxLength);
        builder.Property(v => v.Vin).HasMaxLength(Vehicle.VinLength).IsUnicode(false);
        builder.Property(v => v.PayloadKg).HasPrecision(12, 2);
        builder.Property(v => v.VolumeM3).HasPrecision(9, 2);
        builder.Property(v => v.LoadingMeters).HasPrecision(6, 2);
        builder.Property(v => v.Status).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(v => v.Notes).HasMaxLength(Vehicle.NotesMaxLength);

        builder.HasIndex(v => new { v.TenantId, v.Plate }).IsUnique().HasDatabaseName("UX_Vehicles_Plate");
        builder.HasIndex(v => new { v.TenantId, v.Vin }).IsUnique().HasFilter("[Vin] IS NOT NULL").HasDatabaseName("UX_Vehicles_Vin");
        builder.HasIndex(v => new { v.TenantId, v.CarrierPartyId }).HasDatabaseName("IX_Vehicles_Carrier");
        builder.HasIndex(v => new { v.TenantId, v.NextMaintenanceDate }).HasDatabaseName("IX_Vehicles_NextMaintenance");
    }
}

internal sealed class MaintenanceRecordConfiguration : IEntityTypeConfiguration<MaintenanceRecord>
{
    public void Configure(EntityTypeBuilder<MaintenanceRecord> builder)
    {
        FleetTables.ConfigureCommon(builder, "MaintenanceRecords");
        builder.Property(m => m.KindKey).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Cost).HasPrecision(18, 2);
        builder.Property(m => m.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(m => m.Description).HasMaxLength(MaintenanceRecord.DescriptionMaxLength);

        // Araç bağı (TenantId, Id) üzerinden: başka tenant'ın aracına veritabanı düzeyinde de bağlanılamaz; araç silinmediği için kısıt korur.
        builder.HasOne<Vehicle>().WithMany().HasForeignKey(m => new { m.TenantId, m.VehicleId })
            .HasPrincipalKey(v => new { v.TenantId, v.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => new { m.TenantId, m.VehicleId, m.PerformedOn }).HasDatabaseName("IX_MaintenanceRecords_Vehicle");
    }
}
