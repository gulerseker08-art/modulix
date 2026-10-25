using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.SharedRecords;
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
        builder.Property(e => e.CustomFields);
        builder.HasAlternateKey(e => new { e.TenantId, e.Id });
    }
}

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        CrmTables.ConfigureCommon(builder, "Companies");
        builder.Property(c => c.Industry).HasMaxLength(Company.IndustryMaxLength);
        builder.Property(c => c.Status).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(c => c.Source).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(c => c.Tags).HasMaxLength(Company.TagsMaxLength);
        builder.Property(c => c.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.OwnerId });

        // Ortak taraf (ADR-0006): bir tarafın en fazla bir CRM firması olur. Ünvan ve vergi no tekilliği taraftadır.
        builder.HasOne(c => c.Party)
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.PartyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.PartyId }).IsUnique();
    }
}

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        CrmTables.ConfigureCommon(builder, "Contacts");
        builder.Property(c => c.Notes).HasMaxLength(Company.NotesMaxLength);

        // Ortak kişi (ADR-0006): bir kişinin en fazla bir CRM kişisi olur.
        builder.HasOne(c => c.Person)
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.PersonId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TenantId, c.PersonId }).IsUnique();

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
        builder.Property(o => o.Stage).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(o => o.Amount).HasPrecision(18, 2);
        builder.Property(o => o.Currency).HasMaxLength(Opportunity.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(o => o.LostReason).HasMaxLength(Opportunity.LostReasonMaxLength);
        builder.Property(o => o.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(o => new { o.TenantId, o.Stage });
        builder.HasIndex(o => new { o.TenantId, o.OwnerId });

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
        builder.Property(a => a.Type).HasMaxLength(ChoiceValueKeys.MaxLength).IsRequired();
        builder.Property(a => a.Subject).HasMaxLength(Activity.SubjectMaxLength).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(a => new { a.TenantId, a.DueAt });
        builder.HasIndex(a => new { a.TenantId, a.OwnerId, a.CompletedAt });

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
