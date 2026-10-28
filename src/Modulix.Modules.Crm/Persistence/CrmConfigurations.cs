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
        builder.HasIndex(c => new { c.TenantId, c.OwnerId });

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

internal sealed class CompanyContactConfiguration : IEntityTypeConfiguration<CompanyContact>
{
    public void Configure(EntityTypeBuilder<CompanyContact> builder)
    {
        CrmTables.ConfigureCommon(builder, "CompanyContacts");
        builder.Property(l => l.Department).HasMaxLength(CompanyContact.DepartmentMaxLength);
        builder.Property(l => l.JobTitle).HasMaxLength(CompanyContact.JobTitleMaxLength);
        builder.Property(l => l.Roles).HasMaxLength(CompanyContact.RolesMaxLength);
        builder.HasIndex(l => new { l.TenantId, l.ContactId });

        // Ana irtibat tekilliği: firma başına en fazla bir aktif ana irtibat. Eşzamanlı isteklerde ikincisi 409 alır.
        builder.HasIndex(l => new { l.TenantId, l.CompanyId })
            .IsUnique()
            .HasFilter("[IsPrimary] = 1 AND [IsActive] = 1")
            .HasDatabaseName("UX_CompanyContacts_PrimaryPerCompany");

        // Aynı kişi aynı firmayla aynı anda tek aktif ilişkiye sahip olur; geçmiş ilişkiler çoklanabilir.
        builder.HasIndex(l => new { l.TenantId, l.CompanyId, l.ContactId })
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_CompanyContacts_ActivePair");

        builder.HasOne(l => l.Company)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Contact)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        CrmTables.ConfigureCommon(builder, "Locations");
        builder.Property(l => l.Name).HasMaxLength(Location.NameMaxLength).IsRequired();
        builder.Property(l => l.Code).HasMaxLength(Location.CodeMaxLength);
        builder.Property(l => l.Type).HasMaxLength(30).IsRequired();
        builder.Property(l => l.Notes).HasMaxLength(Company.NotesMaxLength);
        builder.HasIndex(l => new { l.TenantId, l.CompanyId });

        // Kod firma içinde tekildir (boş kodlar çoklanabilir).
        builder.HasIndex(l => new { l.TenantId, l.CompanyId, l.Code })
            .IsUnique()
            .HasFilter("[Code] IS NOT NULL")
            .HasDatabaseName("UX_Locations_CodePerCompany");

        builder.HasOne(l => l.Company)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        CrmTables.ConfigureCommon(builder, "Addresses");
        builder.Property(a => a.Type).HasMaxLength(30).IsRequired();
        builder.Property(a => a.Country).HasMaxLength(Address.CountryLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(a => a.City).HasMaxLength(Address.PartMaxLength);
        builder.Property(a => a.District).HasMaxLength(Address.PartMaxLength);
        builder.Property(a => a.PostalCode).HasMaxLength(Address.PostalCodeMaxLength);
        builder.Property(a => a.Line1).HasMaxLength(Address.LineMaxLength).IsRequired();
        builder.Property(a => a.Line2).HasMaxLength(Address.LineMaxLength);
        builder.Property(a => a.NormalizedText).HasMaxLength(1000).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.CompanyId, a.LocationId });

        // Ana adres firma düzeyinde (LocationId boş) ve her lokasyonda en fazla bir aktiftir; eşzamanlı yazımda ikincisi 409 alır.
        builder.HasIndex(a => new { a.TenantId, a.CompanyId, a.LocationId })
            .IsUnique()
            .HasFilter("[IsPrimary] = 1 AND [IsActive] = 1")
            .HasDatabaseName("UX_Addresses_PrimaryPerOwner");

        builder.HasOne(a => a.Company)
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.LocationId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContactPointConfiguration : IEntityTypeConfiguration<ContactPoint>
{
    public void Configure(EntityTypeBuilder<ContactPoint> builder)
    {
        CrmTables.ConfigureCommon(builder, "ContactPoints");
        builder.Property(p => p.Channel).HasMaxLength(10).IsRequired();
        builder.Property(p => p.Type).HasMaxLength(30).IsRequired();
        builder.Property(p => p.Value).HasMaxLength(ContactPoint.ValueMaxLength).IsRequired();
        builder.Property(p => p.NormalizedValue).HasMaxLength(ContactPoint.NormalizedMaxLength).IsRequired();
        builder.Property(p => p.Note).HasMaxLength(ContactPoint.NoteMaxLength);

        // Sahip firma ya da kişidir; lokasyon noktası firma kimliğini de taşır (kapsam firmadan çözülür). İlişkiye özel nokta kişi sahiplidir.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ContactPoints_Owner",
            "(([CompanyId] IS NOT NULL AND [ContactId] IS NULL) OR ([CompanyId] IS NULL AND [ContactId] IS NOT NULL))"
            + " AND ([LocationId] IS NULL OR [CompanyId] IS NOT NULL) AND ([CompanyContactId] IS NULL OR [ContactId] IS NOT NULL)"));

        // Eşleştirme araması için; benzersiz DEĞİL: aynı e-posta/santral birden fazla kayıtta olabilir (kimlik kanıtı sayılmaz).
        builder.HasIndex(p => new { p.TenantId, p.Channel, p.NormalizedValue });
        builder.HasIndex(p => new { p.TenantId, p.CompanyId });
        builder.HasIndex(p => new { p.TenantId, p.LocationId });
        builder.HasIndex(p => new { p.TenantId, p.ContactId });

        // Ana nokta: sahip (ve kişide ilişki) + kanal başına en fazla bir aktif.
        builder.HasIndex(p => new { p.TenantId, p.CompanyId, p.Channel })
            .IsUnique().HasFilter("[IsPrimary] = 1 AND [IsActive] = 1 AND [CompanyId] IS NOT NULL AND [LocationId] IS NULL")
            .HasDatabaseName("UX_ContactPoints_PrimaryPerCompany");
        builder.HasIndex(p => new { p.TenantId, p.LocationId, p.Channel })
            .IsUnique().HasFilter("[IsPrimary] = 1 AND [IsActive] = 1 AND [LocationId] IS NOT NULL")
            .HasDatabaseName("UX_ContactPoints_PrimaryPerLocation");
        builder.HasIndex(p => new { p.TenantId, p.ContactId, p.CompanyContactId, p.Channel })
            .IsUnique().HasFilter("[IsPrimary] = 1 AND [IsActive] = 1 AND [ContactId] IS NOT NULL")
            .HasDatabaseName("UX_ContactPoints_PrimaryPerContact");

        builder.HasOne<Company>().WithMany().HasForeignKey(p => new { p.TenantId, p.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(p => new { p.TenantId, p.LocationId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(p => new { p.TenantId, p.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CompanyContact>().WithMany().HasForeignKey(p => new { p.TenantId, p.CompanyContactId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CompanyRelationConfiguration : IEntityTypeConfiguration<CompanyRelation>
{
    public void Configure(EntityTypeBuilder<CompanyRelation> builder)
    {
        CrmTables.ConfigureCommon(builder, "CompanyRelations");
        builder.Property(r => r.Type).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(CompanyRelation.NotesMaxLength);
        builder.ToTable(t => t.HasCheckConstraint("CK_CompanyRelations_NoSelf", "[ParentCompanyId] <> [ChildCompanyId]"));
        builder.HasIndex(r => new { r.TenantId, r.ChildCompanyId });
        builder.HasIndex(r => new { r.TenantId, r.ParentCompanyId, r.ChildCompanyId })
            .IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_CompanyRelations_ActivePair");

        builder.HasOne(r => r.ParentCompany).WithMany().HasForeignKey(r => new { r.TenantId, r.ParentCompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.ChildCompany).WithMany().HasForeignKey(r => new { r.TenantId, r.ChildCompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MergedRecordConfiguration : IEntityTypeConfiguration<MergedRecord>
{
    public void Configure(EntityTypeBuilder<MergedRecord> builder)
    {
        CrmTables.ConfigureCommon(builder, "MergedRecords");
        builder.Property(m => m.EntityKey).HasMaxLength(MergedRecord.EntityKeyMaxLength).IsRequired();

        // Bir kayıt yalnızca bir kez birleştirilip kaldırılır; eşzamanlı ikinci birleştirme benzersiz indeksle 409 alır.
        builder.HasIndex(m => new { m.TenantId, m.EntityKey, m.SourceId })
            .IsUnique()
            .HasDatabaseName("UX_MergedRecords_Source");
        builder.HasIndex(m => new { m.TenantId, m.EntityKey, m.TargetId });
    }
}
