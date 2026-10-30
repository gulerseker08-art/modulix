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
        // Bir Lead en fazla bir fırsata dönüşür (dönüşüm iki kez yapılsa bile ikinci fırsat veritabanında da engellenir).
        builder.HasIndex(o => new { o.TenantId, o.SourceLeadId })
            .HasDatabaseName("UX_Opportunities_SourceLead")
            .IsUnique()
            .HasFilter("[SourceLeadId] IS NOT NULL");
        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(o => new { o.TenantId, o.SourceLeadId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

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

        // ADR-0017
        builder.Property(o => o.OpportunityNumber).HasMaxLength(Opportunity.NumberMaxLength).IsRequired();
        builder.Property(o => o.Description).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.Status).HasMaxLength(Opportunity.StatusMaxLength).IsRequired();
        builder.Property(o => o.StatusReason).HasMaxLength(Opportunity.LostReasonMaxLength);
        builder.Property(o => o.Priority).HasMaxLength(10).IsRequired();
        builder.Property(o => o.Type).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(o => o.Source).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(o => o.NeedDescription).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.CurrentProblem).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.ExpectedBenefit).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.Scope).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.SuccessCriteria).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.Risks).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.DecisionProcess).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.TargetDecisionDate).HasColumnType("date");
        builder.Property(o => o.CustomerBudget).HasPrecision(18, 2);
        builder.Property(o => o.BudgetStatus).HasMaxLength(20).IsRequired();
        builder.Property(o => o.ActualCloseDate).HasColumnType("date");
        builder.Property(o => o.WonAmount).HasPrecision(18, 2);
        builder.HasIndex(o => new { o.TenantId, o.PrimaryQuoteId });
        builder.Property(o => o.WonCurrency).HasMaxLength(Opportunity.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(o => o.WinReason).HasMaxLength(Opportunity.LostReasonMaxLength);
        builder.Property(o => o.HandoverNote).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(o => o.LossReason).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(o => o.RecontactDate).HasColumnType("date");
        builder.Property(o => o.HoldReviewDate).HasColumnType("date");
        builder.Property(o => o.NextAction).HasMaxLength(Opportunity.ShortMaxLength);
        builder.Property(o => o.NextActionDate).HasColumnType("date");
        builder.Property(o => o.ExpectedCloseDate).HasColumnType("date");

        // Numara tenant içinde benzersizdir; liste ve rapor sorguları durum/kapanış tarihi ve sorumluya göre çalışır.
        builder.HasIndex(o => new { o.TenantId, o.OpportunityNumber }).HasDatabaseName("UX_Opportunities_Number").IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.Status, o.ExpectedCloseDate });
        builder.HasIndex(o => new { o.TenantId, o.CompanyId });
    }
}

internal sealed class OpportunityContactConfiguration : IEntityTypeConfiguration<OpportunityContact>
{
    public void Configure(EntityTypeBuilder<OpportunityContact> builder)
    {
        CrmTables.ConfigureCommon(builder, "OpportunityContacts");
        builder.Property(l => l.Roles).HasMaxLength(OpportunityContact.RolesMaxLength);
        builder.Property(l => l.Note).HasMaxLength(OpportunityContact.NoteMaxLength);
        // Bir kişi bir fırsata bir kez bağlanır; rolleri bu ilişkide tutulur.
        builder.HasIndex(l => new { l.TenantId, l.OpportunityId, l.ContactId }).IsUnique();
        builder.HasOne<Opportunity>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.OpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Contact)
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityCompetitorConfiguration : IEntityTypeConfiguration<OpportunityCompetitor>
{
    public void Configure(EntityTypeBuilder<OpportunityCompetitor> builder)
    {
        CrmTables.ConfigureCommon(builder, "OpportunityCompetitors");
        builder.Property(c => c.Name).HasMaxLength(Opportunity.ShortMaxLength).IsRequired();
        builder.Property(c => c.Strengths).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(c => c.Weaknesses).HasMaxLength(Opportunity.TextMaxLength);
        builder.Property(c => c.AlternativeAmount).HasPrecision(18, 2);
        builder.Property(c => c.AlternativeCurrency).HasMaxLength(Opportunity.CurrencyLength).IsFixedLength().IsUnicode(false);
        builder.Property(c => c.Notes).HasMaxLength(Opportunity.TextMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.OpportunityId });
        builder.HasOne<Opportunity>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.OpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpportunityHistoryConfiguration : IEntityTypeConfiguration<OpportunityHistory>
{
    public void Configure(EntityTypeBuilder<OpportunityHistory> builder)
    {
        CrmTables.ConfigureCommon(builder, "OpportunityHistory");
        builder.Property(h => h.Kind).HasMaxLength(20).IsRequired();
        builder.Property(h => h.FromStage).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(h => h.ToStage).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(h => h.FromStatus).HasMaxLength(Opportunity.StatusMaxLength);
        builder.Property(h => h.ToStatus).HasMaxLength(Opportunity.StatusMaxLength);
        builder.Property(h => h.FromCloseDate).HasColumnType("date");
        builder.Property(h => h.ToCloseDate).HasColumnType("date");
        builder.Property(h => h.Reason).HasMaxLength(OpportunityHistory.ReasonMaxLength);
        builder.Property(h => h.Details).HasMaxLength(Opportunity.TextMaxLength);
        builder.HasIndex(h => new { h.TenantId, h.OpportunityId, h.At });
        // Tarihçe fırsatsız anlam taşımaz: fırsat deposu silmeden önce tarihçeyi, kişi ve rakip satırlarını kaldırır.
        builder.HasOne<Opportunity>()
            .WithMany()
            .HasForeignKey(h => new { h.TenantId, h.OpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    // Yaşam döngüsü, zaman ve ilişki alanları (ADR-0015).
    private static void ConfigureLifecycle(EntityTypeBuilder<Activity> builder)
    {
        builder.Property(a => a.Status).HasMaxLength(20).IsRequired();
        builder.Property(a => a.StatusReason).HasMaxLength(Activity.ReasonMaxLength);
        builder.Property(a => a.Priority).HasMaxLength(10).IsRequired();
        builder.Property(a => a.StartDate).HasColumnType("date");
        builder.Property(a => a.EndDate).HasColumnType("date");
        builder.Property(a => a.TimeZone).HasMaxLength(Activity.TimeZoneMaxLength);
        builder.Property(a => a.Location).HasMaxLength(Activity.LocationMaxLength);
        builder.Property(a => a.OnlineMeetingUrl).HasMaxLength(Activity.UrlMaxLength);
        builder.Property(a => a.Source).HasMaxLength(20).IsRequired();
        builder.Property(a => a.ExternalSource).HasMaxLength(50);
        builder.Property(a => a.ExternalId).HasMaxLength(Activity.ExternalIdMaxLength);

        // Liste ve takvim sorguları: sorumluya/duruma/zaman aralığına göre. Sorgular sınırlı pencere kullanır.
        builder.HasIndex(a => new { a.TenantId, a.OwnerId, a.Status, a.DueAt });
        builder.HasIndex(a => new { a.TenantId, a.StartAt });
        builder.HasIndex(a => new { a.TenantId, a.StartDate });
        builder.HasIndex(a => new { a.TenantId, a.PreviousActivityId });

        // Aynı dış olayın iki kez üretilmesini veritabanı engeller (webhook / içe aktarma tekrarı).
        builder.HasIndex(a => new { a.TenantId, a.ExternalSource, a.ExternalId })
            .IsUnique()
            .HasFilter("[ExternalSource] IS NOT NULL AND [ExternalId] IS NOT NULL");

        builder.HasOne<Activity>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.PreviousActivityId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

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

        // Lead aktiviteleri (talimat 8): ayrı bir görev sistemi yok, aktivite tablosu Lead'e bağlanır.
        builder.Property(a => a.Outcome).HasMaxLength(30);
        builder.HasIndex(a => new { a.TenantId, a.LeadId });
        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.LeadId })
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureLifecycle(builder);
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

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        CrmTables.ConfigureCommon(builder, "Leads");
        builder.Property(l => l.LeadNumber).HasMaxLength(Lead.NumberMaxLength).IsRequired();
        builder.Property(l => l.Subject).HasMaxLength(Lead.SubjectMaxLength).IsRequired();
        builder.Property(l => l.Description).HasMaxLength(Lead.TextMaxLength);
        builder.Property(l => l.Status).HasMaxLength(30).IsRequired();
        builder.Property(l => l.StatusReason).HasMaxLength(Lead.ReasonMaxLength);
        builder.Property(l => l.Priority).HasMaxLength(20).IsRequired();
        builder.Property(l => l.FirstName).HasMaxLength(Lead.NameMaxLength);
        builder.Property(l => l.LastName).HasMaxLength(Lead.NameMaxLength);
        builder.Property(l => l.CompanyName).HasMaxLength(Lead.ShortMaxLength);
        builder.Property(l => l.Email).HasMaxLength(Lead.EmailMaxLength);
        builder.Property(l => l.NormalizedEmail).HasMaxLength(Lead.EmailMaxLength);
        builder.Property(l => l.Phone).HasMaxLength(Lead.PhoneMaxLength);
        builder.Property(l => l.NormalizedPhone).HasMaxLength(Lead.PhoneMaxLength);
        builder.Property(l => l.JobTitle).HasMaxLength(Lead.NameMaxLength);
        builder.Property(l => l.Website).HasMaxLength(Lead.UrlMaxLength);
        builder.Property(l => l.WebsiteDomain).HasMaxLength(Lead.UrlMaxLength);
        builder.Property(l => l.Country).HasMaxLength(Address.CountryLength).IsFixedLength().IsUnicode(false);
        builder.Property(l => l.City).HasMaxLength(Address.PartMaxLength);
        builder.Property(l => l.ProductInterest).HasMaxLength(Lead.ShortMaxLength);
        builder.Property(l => l.NeedDescription).HasMaxLength(Lead.TextMaxLength);
        builder.Property(l => l.EstimatedBudget).HasPrecision(18, 2);
        builder.Property(l => l.Currency).HasMaxLength(Opportunity.CurrencyLength).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(l => l.Industry).HasMaxLength(Company.IndustryMaxLength);
        builder.Property(l => l.Source).HasMaxLength(ChoiceValueKeys.MaxLength);
        builder.Property(l => l.SourceDetail).HasMaxLength(Lead.ShortMaxLength);
        builder.Property(l => l.Referrer).HasMaxLength(Lead.UrlMaxLength);
        builder.Property(l => l.ExternalSource).HasMaxLength(Lead.UtmMaxLength);
        builder.Property(l => l.ExternalSubmissionId).HasMaxLength(Lead.ExternalIdMaxLength);
        builder.Property(l => l.UtmSource).HasMaxLength(Lead.UtmMaxLength);
        builder.Property(l => l.UtmMedium).HasMaxLength(Lead.UtmMaxLength);
        builder.Property(l => l.UtmCampaign).HasMaxLength(Lead.UtmMaxLength);
        builder.Property(l => l.DisqualificationReason).HasMaxLength(ChoiceValueKeys.MaxLength);

        builder.HasIndex(l => new { l.TenantId, l.LeadNumber }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.Status, l.OwnerId });
        builder.HasIndex(l => new { l.TenantId, l.FirstReceivedAt });
        builder.HasIndex(l => new { l.TenantId, l.NormalizedEmail });
        builder.HasIndex(l => new { l.TenantId, l.NormalizedPhone });

        // Teknik tekrar (aynı gönderimin yeniden iletilmesi) veritabanı düzeyinde de engellenir; boş değerler tekilleştirilmez.
        builder.HasIndex(l => new { l.TenantId, l.ExternalSource, l.ExternalSubmissionId })
            .IsUnique()
            .HasFilter("[ExternalSource] IS NOT NULL AND [ExternalSubmissionId] IS NOT NULL");

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.CompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Dönüşüm izleri: dönüşmüş Lead'in hedef kayıtları silinirse iz kaybolmasın diye silme engellenir.
        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ConvertedCompanyId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ConvertedContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Opportunity>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ConvertedOpportunityId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LeadFormConfiguration : IEntityTypeConfiguration<LeadForm>
{
    public void Configure(EntityTypeBuilder<LeadForm> builder)
    {
        CrmTables.ConfigureCommon(builder, "LeadForms");
        builder.Property(f => f.Name).HasMaxLength(LeadForm.NameMaxLength).IsRequired();
        builder.Property(f => f.FormKey).HasMaxLength(LeadForm.KeyLength).IsRequired();
        builder.Property(f => f.AllowedOrigins).HasMaxLength(LeadForm.OriginsMaxLength);
        builder.Property(f => f.SecretHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        builder.HasIndex(f => new { f.TenantId, f.FormKey }).IsUnique();
    }
}

internal sealed class ActivityLinkConfiguration : IEntityTypeConfiguration<ActivityLink>
{
    public void Configure(EntityTypeBuilder<ActivityLink> builder)
    {
        CrmTables.ConfigureCommon(builder, "ActivityLinks");
        builder.Property(l => l.EntityKey).HasMaxLength(ActivityLink.EntityKeyMaxLength).IsRequired();

        // Aynı kayıt bir aktiviteye iki kez bağlanmaz; birincil ilişki aktivite başına en fazla bir tanedir.
        builder.HasIndex(l => new { l.TenantId, l.ActivityId, l.EntityKey, l.RecordId }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.ActivityId }).IsUnique().HasFilter("[IsPrimary] = 1")
            .HasDatabaseName("UX_ActivityLinks_Primary");

        // Zaman akışı: bir kaydın aktivitelerini bulmak.
        builder.HasIndex(l => new { l.TenantId, l.EntityKey, l.RecordId });

        builder.HasOne<Activity>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.ActivityId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActivityParticipantConfiguration : IEntityTypeConfiguration<ActivityParticipant>
{
    public void Configure(EntityTypeBuilder<ActivityParticipant> builder)
    {
        CrmTables.ConfigureCommon(builder, "ActivityParticipants");
        builder.Property(p => p.Email).HasMaxLength(ActivityParticipant.EmailMaxLength);
        builder.Property(p => p.NormalizedEmail).HasMaxLength(ActivityParticipant.EmailMaxLength);
        builder.Property(p => p.DisplayName).HasMaxLength(ActivityParticipant.NameMaxLength);
        builder.Property(p => p.Role).HasMaxLength(10).IsRequired();

        builder.HasIndex(p => new { p.TenantId, p.ActivityId, p.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL");
        builder.HasIndex(p => new { p.TenantId, p.ActivityId, p.ContactId }).IsUnique().HasFilter("[ContactId] IS NOT NULL");
        builder.HasIndex(p => new { p.TenantId, p.ActivityId, p.NormalizedEmail }).IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
        // "Katıldığım aktiviteler" ve özel aktivite görünürlüğü.
        builder.HasIndex(p => new { p.TenantId, p.UserId });

        builder.HasOne<Activity>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ActivityId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.ContactId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
