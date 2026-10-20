using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Kayıt türleri: "crm.company", "crm.contact", "crm.opportunity", "crm.activity".
// Alan adları API'de ve audit kaydında görünen teknik kimliklerdir; değiştirmeyin.

internal sealed class CompanyRecordStore(IModuleData data) : CrmRecordStore<Company>(data)
{
    public override string EntityKey => CrmModule.Entities.Company;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override IReadOnlyList<RecordField<Company>> Fields { get; } =
    [
        Field.Text<Company>("name", Company.NameMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Text<Company>("taxNumber", Company.TaxNumberMaxLength, c => c.TaxNumber, (c, v) => c.TaxNumber = v),
        Field.Text<Company>("phone", Company.PhoneMaxLength, c => c.Phone, (c, v) => c.Phone = v),
        Field.Email<Company>("email", Company.EmailMaxLength, c => c.Email, (c, v) => c.Email = v),
        Field.Text<Company>("website", Company.WebsiteMaxLength, c => c.Website, (c, v) => c.Website = v),
        Field.Text<Company>("city", Company.CityMaxLength, c => c.City, (c, v) => c.City = v),
        Field.Text<Company>("notes", Company.NotesMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IOrderedQueryable<Company> Order(IQueryable<Company> source) => source.OrderBy(c => c.Name);

    protected override IQueryable<Company> Search(IQueryable<Company> source, string term) =>
        source.Where(c => c.Name.Contains(term) || (c.TaxNumber != null && c.TaxNumber.Contains(term)) || (c.City != null && c.City.Contains(term)));
}

internal sealed class ContactRecordStore(IModuleData data) : CrmRecordStore<Contact>(data)
{
    public override string EntityKey => CrmModule.Entities.Contact;

    protected override string ReadPermission => CrmModule.Permissions.ContactsRead;

    protected override string WritePermission => CrmModule.Permissions.ContactsWrite;

    protected override IReadOnlyList<RecordField<Contact>> Fields { get; } =
    [
        Field.Text<Contact>("firstName", Contact.NameMaxLength, c => c.FirstName, (c, v) => c.FirstName = v!, required: true),
        Field.Text<Contact>("lastName", Contact.NameMaxLength, c => c.LastName, (c, v) => c.LastName = v!, required: true),
        Field.Reference<Contact, Company>("companyId", "Firma bulunamadı.", c => c.CompanyId, (c, v) => c.CompanyId = v),
        Field.Text<Contact>("title", Contact.TitleMaxLength, c => c.Title, (c, v) => c.Title = v),
        Field.Email<Contact>("email", Company.EmailMaxLength, c => c.Email, (c, v) => c.Email = v),
        Field.Text<Contact>("phone", Company.PhoneMaxLength, c => c.Phone, (c, v) => c.Phone = v),
        Field.Text<Contact>("notes", Company.NotesMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IOrderedQueryable<Contact> Order(IQueryable<Contact> source) =>
        source.OrderBy(c => c.LastName).ThenBy(c => c.FirstName);

    protected override IQueryable<Contact> Search(IQueryable<Contact> source, string term) =>
        source.Where(c => c.FirstName.Contains(term) || c.LastName.Contains(term) || (c.Email != null && c.Email.Contains(term)));
}

internal sealed class OpportunityRecordStore(IModuleData data) : CrmRecordStore<Opportunity>(data)
{
    public override string EntityKey => CrmModule.Entities.Opportunity;

    protected override string ReadPermission => CrmModule.Permissions.OpportunitiesRead;

    protected override string WritePermission => CrmModule.Permissions.OpportunitiesWrite;

    protected override IReadOnlyList<RecordField<Opportunity>> Fields { get; } =
    [
        Field.Text<Opportunity>("title", Opportunity.TitleMaxLength, o => o.Title, (o, v) => o.Title = v!, required: true),
        Field.Reference<Opportunity, Company>("companyId", "Firma bulunamadı.", o => o.CompanyId, (o, v) => o.CompanyId = v!.Value, required: true),
        Field.Reference<Opportunity, Contact>("contactId", "Kişi bulunamadı.", o => o.ContactId, (o, v) => o.ContactId = v),
        Field.Choice<Opportunity, OpportunityStage>("stage", o => o.Stage, (o, v) => o.Stage = v),
        Field.Money<Opportunity>("amount", o => o.Amount, (o, v) => o.Amount = v),
        Field.Currency<Opportunity>("currency", o => o.Currency, (o, v) => o.Currency = v),
        Field.Date<Opportunity>("expectedCloseDate", o => o.ExpectedCloseDate, (o, v) => o.ExpectedCloseDate = v),
        Field.Text<Opportunity>("notes", Company.NotesMaxLength, o => o.Notes, (o, v) => o.Notes = v),
    ];

    protected override IOrderedQueryable<Opportunity> Order(IQueryable<Opportunity> source) =>
        source.OrderByDescending(o => o.CreatedAt);

    protected override IQueryable<Opportunity> Search(IQueryable<Opportunity> source, string term) =>
        source.Where(o => o.Title.Contains(term));
}

internal sealed class ActivityRecordStore(IModuleData data) : CrmRecordStore<Activity>(data)
{
    public override string EntityKey => CrmModule.Entities.Activity;

    protected override string ReadPermission => CrmModule.Permissions.ActivitiesRead;

    protected override string WritePermission => CrmModule.Permissions.ActivitiesWrite;

    protected override IReadOnlyList<RecordField<Activity>> Fields { get; } =
    [
        Field.Choice<Activity, ActivityType>("type", a => a.Type, (a, v) => a.Type = v),
        Field.Text<Activity>("subject", Activity.SubjectMaxLength, a => a.Subject, (a, v) => a.Subject = v!, required: true),
        Field.Time<Activity>("dueAt", a => a.DueAt, (a, v) => a.DueAt = v),
        Field.Time<Activity>("completedAt", a => a.CompletedAt, (a, v) => a.CompletedAt = v),
        Field.Reference<Activity, Company>("companyId", "Firma bulunamadı.", a => a.CompanyId, (a, v) => a.CompanyId = v),
        Field.Reference<Activity, Contact>("contactId", "Kişi bulunamadı.", a => a.ContactId, (a, v) => a.ContactId = v),
        Field.Reference<Activity, Opportunity>("opportunityId", "Fırsat bulunamadı.", a => a.OpportunityId, (a, v) => a.OpportunityId = v),
        Field.Text<Activity>("notes", Company.NotesMaxLength, a => a.Notes, (a, v) => a.Notes = v),
    ];

    /// <summary>Açık aktiviteler önce, en yakın tarihli üstte; tarihsizler sonda.</summary>
    protected override IOrderedQueryable<Activity> Order(IQueryable<Activity> source) =>
        source.OrderBy(a => a.CompletedAt != null).ThenBy(a => a.DueAt == null).ThenBy(a => a.DueAt);

    protected override IQueryable<Activity> Search(IQueryable<Activity> source, string term) =>
        source.Where(a => a.Subject.Contains(term));
}
