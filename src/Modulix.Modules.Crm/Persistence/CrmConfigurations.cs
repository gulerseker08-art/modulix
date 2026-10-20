using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Persistence;

// CRM tabloları "crm" şemasındadır ve tenant verisidir: sorgu filtresi otomatik uygulanır, migration'da
// her tablo Row-Level Security politikasına eklenir (TenantIsolationSql.AddTable).
// Kayıtlar arası bağlar (TenantId, Id) üzerinden kurulur: başka tenant'ın kaydına veritabanı düzeyinde de bağlanılamaz.

internal static class CrmTables
{
    public const string Schema = CrmModule.ModuleKey;

    /// <summary>Ortak alanlar; bileşik anahtar diğer CRM tablolarının aynı tenant'a bağlanması içindir.</summary>
    public static void ConfigureCommon<TEntity>(EntityTypeBuilder<TEntity> builder, string table)
        where TEntity : CrmEntity
    {
        builder.ToTable(table, Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        CrmTables.ConfigureCommon(builder, "Companies");
        builder.Property(c => c.Name).HasMaxLength(Company.NameMaxLength).IsRequired();
        builder.Property(c => c.TaxNumber).HasMaxLength(Company.TaxNumberMaxLength);
        builder.Property(c => c.Phone).HasMaxLength(Company.PhoneMaxLength);
        builder.Property(c => c.Email).HasMaxLength(Company.EmailMaxLength);
        builder.Property(c => c.Website).HasMaxLength(Company.WebsiteMaxLength);
        builder.Property(c => c.City).HasMaxLength(Company.CityMaxLength);
        builder.Property(c => c.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.Name });

        // Vergi numarası tenant içinde tekildir; boş bırakılan kayıtlar bu kurala girmez (SQL Server filtreli indeks üretir).
        builder.HasIndex(c => new { c.TenantId, c.TaxNumber }).IsUnique();
    }
}

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        CrmTables.ConfigureCommon(builder, "Contacts");
        builder.Property(c => c.FirstName).HasMaxLength(Contact.NameMaxLength).IsRequired();
        builder.Property(c => c.LastName).HasMaxLength(Contact.NameMaxLength).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(Contact.TitleMaxLength);
        builder.Property(c => c.Email).HasMaxLength(Company.EmailMaxLength);
        builder.Property(c => c.Phone).HasMaxLength(Company.PhoneMaxLength);
        builder.Property(c => c.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.LastName, c.FirstName });

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityConfiguration : IEntityTypeConfiguration<Opportunity>
{
    public void Configure(EntityTypeBuilder<Opportunity> builder)
    {
        CrmTables.ConfigureCommon(builder, "Opportunities");
        builder.Property(o => o.Title).HasMaxLength(Opportunity.TitleMaxLength).IsRequired();
        builder.Property(o => o.Stage).IsRequired();
        builder.Property(o => o.Amount).HasPrecision(18, 2);
        builder.Property(o => o.Currency).HasMaxLength(Opportunity.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(o => o.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(o => new { o.TenantId, o.Stage });

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(o => new { o.TenantId, o.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(o => new { o.TenantId, o.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> builder)
    {
        CrmTables.ConfigureCommon(builder, "Activities");
        builder.Property(a => a.Type).IsRequired();
        builder.Property(a => a.Subject).HasMaxLength(Activity.SubjectMaxLength).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(a => new { a.TenantId, a.DueAt });

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Opportunity>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.OpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
