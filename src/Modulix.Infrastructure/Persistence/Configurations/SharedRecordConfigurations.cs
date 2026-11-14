using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.SharedRecords;

namespace Modulix.Infrastructure.Persistence.Configurations;

// Ortak kayıtlar (ADR-0006): tenant verisi, core şemasında; Row-Level Security ile korunur (migration: TenantIsolationSql).
// Modül uzantıları bu tablolara (TenantId, Id) üzerinden bağlanır: başka tenant'ın kaydına veritabanı düzeyinde de bağlanılamaz.

internal static class SharedRecordTables
{
    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : SharedEntity
    {
        builder.ToTable(table, DatabaseSchemas.Core);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> builder)
    {
        SharedRecordTables.ConfigureCommon(builder, "Parties");
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Party.NameMaxLength).IsRequired();
        builder.Property(p => p.TaxOffice).HasMaxLength(Party.TaxOfficeMaxLength);
        builder.Property(p => p.TaxNumber).HasMaxLength(Party.TaxNumberMaxLength);
        builder.Property(p => p.Phone).HasMaxLength(Party.PhoneMaxLength);
        builder.Property(p => p.Email).HasMaxLength(Party.EmailMaxLength);
        builder.Property(p => p.Website).HasMaxLength(Party.WebsiteMaxLength);
        builder.Property(p => p.City).HasMaxLength(Party.CityMaxLength);
        builder.Property(p => p.Address).HasMaxLength(Party.AddressMaxLength);
        builder.Property(p => p.IndustryCode).HasMaxLength(Party.IndustryCodeMaxLength);

        // Sektör (ADR-0043): platform kataloğuna kodla başvurur; katalog satırı silinmez (pasife alınır), bu yüzden Restrict.
        builder.HasOne(p => p.Industry).WithMany().HasForeignKey(p => p.IndustryCode).HasPrincipalKey(i => i.Code).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TenantId, p.IndustryCode });
        builder.Property(p => p.RelationshipRoles).HasMaxLength(Party.CodeListMaxLength);
        builder.Property(p => p.CustomerSectors).HasMaxLength(Party.CodeListMaxLength);
        builder.Property(p => p.ServiceNeeds).HasMaxLength(Party.CodeListMaxLength);
        builder.HasIndex(p => new { p.TenantId, p.Name });

        // Vergi numarası tenant içinde tekildir; boş bırakılan kayıtlar bu kurala girmez (SQL Server filtreli indeks üretir).
        builder.HasIndex(p => new { p.TenantId, p.TaxNumber }).IsUnique();
    }
}

internal sealed class PartyRelationshipConfiguration : IEntityTypeConfiguration<PartyRelationship>
{
    public void Configure(EntityTypeBuilder<PartyRelationship> builder)
    {
        SharedRecordTables.ConfigureCommon(builder, "PartyRelationships");
        builder.Property(r => r.RoleCode).HasMaxLength(PartyRelationship.RoleCodeMaxLength).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(PartyRelationship.NotesMaxLength);

        // Firma + rol başına tek kayıt.
        builder.HasIndex(r => new { r.TenantId, r.PartyId, r.RoleCode }).IsUnique();

        // İlişki kaydı firmanın alt verisidir: firma silinince (ya da başka firmaya birleştirilince) birlikte gider.
        builder.HasOne<Party>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        SharedRecordTables.ConfigureCommon(builder, "People");
        builder.Property(p => p.FirstName).HasMaxLength(Person.NameMaxLength).IsRequired();
        builder.Property(p => p.LastName).HasMaxLength(Person.NameMaxLength).IsRequired();
        builder.Property(p => p.Email).HasMaxLength(Person.EmailMaxLength);
        builder.Property(p => p.Phone).HasMaxLength(Person.PhoneMaxLength);
        builder.Property(p => p.Title).HasMaxLength(Person.TitleMaxLength);
        builder.HasIndex(p => new { p.TenantId, p.LastName, p.FirstName });
        builder.HasIndex(p => new { p.TenantId, p.Email });

        builder.HasOne<Party>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PlaceConfiguration : IEntityTypeConfiguration<Place>
{
    public void Configure(EntityTypeBuilder<Place> builder)
    {
        SharedRecordTables.ConfigureCommon(builder, "Places");
        builder.Property(p => p.Name).HasMaxLength(Place.NameMaxLength).IsRequired();
        builder.Property(p => p.NormalizedName).HasMaxLength(Place.NameMaxLength).IsRequired();
        builder.Property(p => p.KindKey).HasMaxLength(Place.KindKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(p => p.CountryCode).HasMaxLength(Place.CountryLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(p => p.City).HasMaxLength(Place.CityMaxLength);
        builder.Property(p => p.District).HasMaxLength(Place.CityMaxLength);
        builder.Property(p => p.AddressLine).HasMaxLength(Place.AddressMaxLength);
        builder.Property(p => p.PostalCode).HasMaxLength(Place.PostalCodeMaxLength);
        builder.Property(p => p.Latitude).HasPrecision(9, 6);
        builder.Property(p => p.Longitude).HasPrecision(9, 6);

        // Ad ülke içinde tekildir (ADR-0045 K1).
        builder.HasIndex(p => new { p.TenantId, p.NormalizedName, p.CountryCode }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.CountryCode, p.City });
    }
}
