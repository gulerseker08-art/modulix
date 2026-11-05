using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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
        builder.Property(f => f.IsActive).HasDefaultValue(true);

        builder.HasIndex(f => new { f.TenantId, f.Code }).IsUnique().HasDatabaseName("UX_Facilities_Code");
    }
}
