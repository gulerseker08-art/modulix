using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Customs;
using Modulix.Core.Contracts.SharedRecords;
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

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "Locations");
        builder.Property(l => l.Value).HasMaxLength(Location.ValueMaxLength).IsRequired();
        builder.Property(l => l.Code).HasMaxLength(Location.CodeMaxLength).IsRequired();
        builder.Property(l => l.Path).HasMaxLength(Location.PathMaxLength).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(Location.NameMaxLength);
        builder.Property(l => l.IsActive).HasDefaultValue(true);

        // Kod tesis içinde benzersizdir (kardeşler arası benzersizlik de buradan gelir).
        builder.HasIndex(l => new { l.TenantId, l.FacilityId, l.Code }).IsUnique().HasDatabaseName("UX_Locations_Code");
        builder.HasIndex(l => new { l.TenantId, l.FacilityId, l.ParentId });
        builder.HasIndex(l => new { l.TenantId, l.ZoneId });

        // Tesis, üst düğüm ve alan aynı tenant'ta olmalıdır; alt düğümü, alanı ya da seviyesi olan kayıt silinemez.
        builder.HasOne<Facility>().WithMany().HasForeignKey(l => new { l.TenantId, l.FacilityId })
            .HasPrincipalKey(f => new { f.TenantId, f.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(l => new { l.TenantId, l.ParentId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Zone>().WithMany().HasForeignKey(l => new { l.TenantId, l.ZoneId })
            .HasPrincipalKey(z => new { z.TenantId, z.Id }).OnDelete(DeleteBehavior.Restrict);
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

internal sealed class PowerOfAttorneyConfiguration : IEntityTypeConfiguration<PowerOfAttorney>
{
    public void Configure(EntityTypeBuilder<PowerOfAttorney> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "PowersOfAttorney");
        builder.Property(p => p.ValidFrom).IsRequired();
        builder.Property(p => p.Scope).HasMaxLength(PowerOfAttorney.ScopeMaxLength);
        builder.Property(p => p.DocumentReference).HasMaxLength(PowerOfAttorney.DocumentMaxLength);
        builder.Property(p => p.IsActive).HasDefaultValue(true);
        builder.ToTable(t => t.HasCheckConstraint("CK_PowersOfAttorney_Dates", "[ValidTo] IS NULL OR [ValidTo] >= [ValidFrom]"));

        // Müşavirin hangi ithalatçılar için geçerli vekâleti olduğunu bulmak için (ASN kapsamı).
        builder.HasIndex(p => new { p.TenantId, p.BrokerPartyId, p.ImporterPartyId });
        builder.HasIndex(p => new { p.TenantId, p.ImporterPartyId });

        // Ortak kayıtlar (ADR-0006): ithalatçı ve müşavir aynı tenant'ın firmasıdır; vekâleti olan firma silinemez.
        builder.HasOne<Party>().WithMany().HasForeignKey(p => new { p.TenantId, p.ImporterPartyId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(p => new { p.TenantId, p.BrokerPartyId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "StockItems");
        builder.Property(s => s.Code).HasMaxLength(StockItem.CodeMaxLength).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(StockItem.NameMaxLength).IsRequired();
        builder.Property(s => s.UnitKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(StockItem.DescriptionMaxLength);
        // GTİP metin: baştaki sıfırlar kaybolmasın (K9). Unicode değil, rakamlardan oluşur.
        builder.Property(s => s.Gtip).HasMaxLength(GtipRules.MaxLength).IsUnicode(false);
        builder.Property(s => s.IsActive).HasDefaultValue(true);

        builder.HasIndex(s => new { s.TenantId, s.Code }).IsUnique().HasDatabaseName("UX_StockItems_Code");
        builder.HasIndex(s => new { s.TenantId, s.Gtip });
    }
}

internal sealed class CustomsDeclarationConfiguration : IEntityTypeConfiguration<CustomsDeclaration>
{
    public void Configure(EntityTypeBuilder<CustomsDeclaration> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "CustomsDeclarations");
        builder.Property(d => d.RegistrationNo).HasMaxLength(CustomsDeclaration.RegistrationNoMaxLength).IsRequired();
        builder.Property(d => d.RegistrationDate).HasColumnType("date");
        builder.Property(d => d.CustomsOffice).HasMaxLength(CustomsDeclaration.OfficeMaxLength);
        builder.Property(d => d.RegimeKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(d => d.StatusKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(d => d.ExternalReference).HasMaxLength(CustomsDeclaration.ExternalReferenceMaxLength);
        builder.Property(d => d.Description).HasMaxLength(CustomsDeclaration.DescriptionMaxLength);

        builder.HasIndex(d => new { d.TenantId, d.RegistrationNo }).IsUnique().HasDatabaseName("UX_CustomsDeclarations_RegistrationNo");

        // Dış sistemden gelen aynı kayıt ikinci beyanname üretmez (K8); boş referanslar çakışmaz.
        builder.HasIndex(d => new { d.TenantId, d.ExternalReference }).IsUnique().HasDatabaseName("UX_CustomsDeclarations_ExternalReference")
            .HasFilter("[ExternalReference] IS NOT NULL");
        builder.HasIndex(d => new { d.TenantId, d.ImporterPartyId });
        builder.HasIndex(d => new { d.TenantId, d.BrokerPartyId });
        builder.HasIndex(d => new { d.TenantId, d.RegistrationDate });

        // Ortak kayıtlar (ADR-0006): ithalatçı ve müşavir aynı tenant'ın firmasıdır; beyannamesi olan firma silinemez.
        builder.HasOne<Party>().WithMany().HasForeignKey(d => new { d.TenantId, d.ImporterPartyId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Party>().WithMany().HasForeignKey(d => new { d.TenantId, d.BrokerPartyId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RegimeBindingConfiguration : IEntityTypeConfiguration<RegimeBinding>
{
    public void Configure(EntityTypeBuilder<RegimeBinding> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "RegimeBindings");
        builder.Property(b => b.RegimeKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(b => b.Enforcement).HasMaxLength(10).IsRequired();

        // Rejim başına tek bağlama. Profil platform tablosundadır; modül Core'a başvurmadığı için yabancı anahtar yoktur (profil silinmez, pasife alınır).
        builder.HasIndex(b => new { b.TenantId, b.RegimeKey }).IsUnique().HasDatabaseName("UX_RegimeBindings_RegimeKey");
    }
}

internal sealed class CustomsDeclarationLineConfiguration : IEntityTypeConfiguration<CustomsDeclarationLine>
{
    public void Configure(EntityTypeBuilder<CustomsDeclarationLine> builder)
    {
        WarehouseTables.ConfigureCommon(builder, "CustomsDeclarationLines");
        // GTİP metin: baştaki sıfırlar kaybolmasın (K9).
        builder.Property(l => l.Gtip).HasMaxLength(GtipRules.MaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(CustomsDeclarationLine.DescriptionMaxLength).IsRequired();
        builder.Property(l => l.ContainerTypeKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(l => l.OriginCountryKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(l => l.UnitKey).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(l => l.GrossKg).HasPrecision(18, 3);
        builder.Property(l => l.NetKg).HasPrecision(18, 3);
        builder.Property(l => l.Quantity).HasPrecision(18, 3);

        // Net kilo brüt kilodan büyük olamaz (K17 değişmezi) ve satır numarası beyanname içinde benzersizdir.
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomsDeclarationLines_Weights", "[NetKg] >= 0 AND [NetKg] <= [GrossKg]"));
        builder.HasIndex(l => new { l.TenantId, l.DeclarationId, l.LineNo }).IsUnique().HasDatabaseName("UX_CustomsDeclarationLines_LineNo");
        builder.HasIndex(l => new { l.TenantId, l.Gtip });

        // Beyanname aynı tenant'ta olmalıdır; satırı olan beyanname silinemez (önce satırlar silinir).
        builder.HasOne<CustomsDeclaration>().WithMany().HasForeignKey(l => new { l.TenantId, l.DeclarationId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
