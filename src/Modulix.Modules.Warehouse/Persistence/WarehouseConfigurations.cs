using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customization;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Persistence;

// Depo tabloları "warehouse" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da her tablo
// Row-Level Security politikasına eklenir (TenantIsolationSql.AddTable). Benzersizlik kuralları TenantId ile birlikte tanımlanır.
// Kayıtlar arası bağlar (TenantId, Id) üzerinden kurulur: başka tenant'ın kaydına veritabanı düzeyinde de bağlanılamaz.

internal static class WarehouseTables
{
    public const string Schema = WarehouseModule.ModuleKey;

    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : WarehouseEntity
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

internal sealed class FacilityConfiguration : IEntityTypeConfiguration<Facility>
{
    public void Configure(EntityTypeBuilder<Facility> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "Facilities");
        builder.Property(f => f.Code).HasMaxLength(Facility.CodeMaxLength).IsRequired();
        builder.Property(f => f.Name).HasMaxLength(Facility.NameMaxLength).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(Facility.DescriptionMaxLength);
        builder.Property(f => f.CountryCode).HasMaxLength(Facility.CountryCodeLength).IsFixedLength().IsUnicode(false);
        builder.Property(f => f.CustomsEnabled).HasDefaultValue(false);
        builder.Property(f => f.AllowMixedZones).HasDefaultValue(false);
        builder.Property(f => f.IsActive).HasDefaultValue(true);

        builder.HasIndex(f => new { f.TenantId, f.Code }).IsUnique().HasDatabaseName("UX_Facilities_Code");
    }
}

internal sealed class LocationLevelConfiguration : IEntityTypeConfiguration<LocationLevel>
{
    public void Configure(EntityTypeBuilder<LocationLevel> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "LocationLevels");
        builder.Property(l => l.Key).HasMaxLength(LocationLevel.KeyMaxLength).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(LocationLevel.NameMaxLength).IsRequired();
        builder.Property(l => l.CharacterSet).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(l => l.Separator).HasMaxLength(LocationLevel.SeparatorMaxLength).IsRequired();

        builder.HasIndex(l => new { l.TenantId, l.FacilityId, l.Position }).IsUnique().HasDatabaseName("UX_LocationLevels_Position");
        builder.HasIndex(l => new { l.TenantId, l.FacilityId, l.Key }).IsUnique().HasDatabaseName("UX_LocationLevels_Key");

        // Tesis aynı tenant'ta olmalıdır; seviye tanımı olan tesis silinemez.
        builder.HasOne<Facility>().WithMany().HasForeignKey(l => new { l.TenantId, l.FacilityId })
            .HasPrincipalKey(f => new { f.TenantId, f.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "Zones");
        builder.Property(z => z.Code).HasMaxLength(Zone.CodeMaxLength).IsRequired();
        builder.Property(z => z.Name).HasMaxLength(Zone.NameMaxLength).IsRequired();
        builder.Property(z => z.KindKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(z => z.Description).HasMaxLength(Zone.DescriptionMaxLength);
        builder.Property(z => z.IsActive).HasDefaultValue(true);

        builder.HasIndex(z => new { z.TenantId, z.FacilityId, z.Code }).IsUnique().HasDatabaseName("UX_Zones_Code");

        // Tesis aynı tenant'ta olmalıdır; alanı olan tesis silinemez.
        builder.HasOne<Facility>().WithMany().HasForeignKey(z => new { z.TenantId, z.FacilityId })
            .HasPrincipalKey(f => new { f.TenantId, f.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
