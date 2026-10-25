using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Kayıt türleri: "crm.company", "crm.contact", "crm.opportunity", "crm.activity".
// Alan ve filtre adları API'de ve audit kaydında görünen teknik kimliklerdir; değiştirmeyin.
// Sorumlu filtresi "ownerId=me" isteği yapan kullanıcıyı seçer.
// Firma ve kişi ortak kaydın uzantısıdır (ADR-0006): ad, vergi no, iletişim gibi alanlar API'de aynı adlarla durur ama
// ortak kayda (Party, Person) yazılır; aynı kaydı diğer modüller de kullanır.

internal sealed class CompanyRecordStore(CrmStoreContext context) : CrmRecordStore<Company>(context)
{
    /// <summary>Taraftaki alanlar. Mevcut tarafa bağlanırken boş gönderilenler taraftaki değeri silmez.</summary>
    private static readonly string[] PartyFields = ["name", "taxNumber", "phone", "email", "website", "city", "address"];

    public override string EntityKey => CrmModule.Entities.Company;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override IReadOnlyList<RecordField<Company>> Fields { get; } =
    [
        Field.SharedId<Company>("partyId", c => c.PartyId),
        Field.Text<Company>("name", Party.NameMaxLength, c => c.Party.Name, (c, v) => c.Party.Name = v!, required: true),
        Field.Text<Company>("taxNumber", Party.TaxNumberMaxLength, c => c.Party.TaxNumber, (c, v) => c.Party.TaxNumber = v),
        Field.Text<Company>("phone", Party.PhoneMaxLength, c => c.Party.Phone, (c, v) => c.Party.Phone = v),
        Field.Email<Company>("email", Party.EmailMaxLength, c => c.Party.Email, (c, v) => c.Party.Email = v),
        Field.Text<Company>("website", Party.WebsiteMaxLength, c => c.Party.Website, (c, v) => c.Party.Website = v),
        Field.Text<Company>("city", Party.CityMaxLength, c => c.Party.City, (c, v) => c.Party.City = v),
        Field.Text<Company>("address", Party.AddressMaxLength, c => c.Party.Address, (c, v) => c.Party.Address = v),
        Field.Text<Company>("industry", Company.IndustryMaxLength, c => c.Industry, (c, v) => c.Industry = v),
        Field.ListChoice<Company>("status", CrmModule.Lists.CompanyStatus, c => c.Status, (c, v) => c.Status = v!),
        Field.ListChoice<Company>("source", CrmModule.Lists.CompanySource, c => c.Source, (c, v) => c.Source = v, required: false),
        Field.Text<Company>("tags", Company.TagsMaxLength, c => c.Tags, (c, v) => c.Tags = v),
        Field.Owner<Company>(OwnerField, c => c.OwnerId, (c, v) => c.OwnerId = v),
        Field.Text<Company>("notes", Company.NotesMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Company>> Filters { get; } = new Dictionary<string, RecordFilter<Company>>
    {
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(c => c.OwnerId == owner) : null,
        ["partyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PartyId == id) : null,
    };

    protected override IReadOnlyDictionary<string, AsyncRecordFilter<Company>> AsyncFilters { get; } =
        new Dictionary<string, AsyncRecordFilter<Company>>
        {
            ["status"] = async (q, v, ctx, ct) =>
                await FilterValue.ChoiceAsync(v, CrmModule.Lists.CompanyStatus, ctx, ct) is { } status ? q.Where(c => c.Status == status) : null,
        };

    protected override IQueryable<Company> Load(IQueryable<Company> source) => source.Include(c => c.Party);

    protected override IOrderedQueryable<Company> Order(IQueryable<Company> source) => source.OrderBy(c => c.Party.Name);

    protected override IQueryable<Company> Search(IQueryable<Company> source, string term) =>
        source.Where(c => c.Party.Name.Contains(term)
            || (c.Party.TaxNumber != null && c.Party.TaxNumber.Contains(term))
            || (c.Party.City != null && c.Party.City.Contains(term))
            || (c.Tags != null && c.Tags.Contains(term)));

    /// <summary>
    /// Yeni firma bir tarafa bağlanır: "partyId" verildiyse o tarafa, aynı vergi numaralı taraf varsa ona (vergi
    /// numarası kesin eşleşmedir, ör. Lojistik'te açılmış müşteri CRM'e eklenir), yoksa yeni tarafa. Tarafın zaten bir
    /// CRM firması varsa doğrulama hatası. Mevcut tarafa bağlanırken boş gönderilen taraf alanları değeri silmez.
    /// </summary>
    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Company entity, JsonObject values, CancellationToken cancellationToken)
    {
        var parties = Context.Data.Set<Party>();
        var (field, party) = FieldNamed("partyId").Parse(values["partyId"]).Value is Guid partyId
            ? ("partyId", await parties.SingleOrDefaultAsync(p => p.Id == partyId, cancellationToken)
                ?? throw new RecordValidationException([new RecordFieldError("partyId", "Firma bulunamadı.")]))
            : FieldNamed("taxNumber").Parse(values["taxNumber"]).Value is string taxNumber
                ? ("taxNumber", await parties.SingleOrDefaultAsync(p => p.TaxNumber == taxNumber, cancellationToken))
                : ("taxNumber", null);

        if (party is null)
        {
            entity.AttachParty(new Party());
            return (values, false);
        }

        if (await Context.Data.Set<Company>().AnyAsync(c => c.PartyId == party.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError(field,
                field == "partyId" ? "Bu firma CRM'de zaten kayıtlı." : "Bu vergi numarasıyla kayıtlı bir firma var.")]);
        }

        entity.AttachParty(party);
        var kept = values.DeepClone().AsObject();
        foreach (var name in PartyFields.Where(n => kept.TryGetPropertyValue(n, out var node) && FieldNamed(n).Parse(node).Value is null))
        {
            kept.Remove(name);
        }

        return (kept, true);
    }

    /// <summary>Vergi numarası taraflar arasında tekildir; başka bir tarafın numarası verilirse alan hatası (409 değil).</summary>
    protected override async Task AppliedAsync(Company entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (entity.Party.TaxNumber is { } number
            && await Context.Data.Set<Party>().AnyAsync(p => p.TaxNumber == number && p.Id != entity.PartyId, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("taxNumber", "Bu vergi numarasıyla kayıtlı bir firma var.")]);
        }
    }

    protected override SharedEntity SharedOf(Company entity) => entity.Party;

    protected override JsonObject SharedState(Company entity) => new()
    {
        ["name"] = entity.Party.Name, ["taxNumber"] = entity.Party.TaxNumber, ["phone"] = entity.Party.Phone,
        ["email"] = entity.Party.Email, ["website"] = entity.Party.Website, ["city"] = entity.Party.City, ["address"] = entity.Party.Address,
    };
}

internal sealed class ContactRecordStore(CrmStoreContext context) : CrmRecordStore<Contact>(context)
{

    public override string EntityKey => CrmModule.Entities.Contact;

    protected override string ReadPermission => CrmModule.Permissions.ContactsRead;

    protected override string WritePermission => CrmModule.Permissions.ContactsWrite;

    protected override IReadOnlyList<RecordField<Contact>> Fields { get; } =
    [
        Field.SharedId<Contact>("personId", c => c.PersonId),
        Field.Text<Contact>("firstName", Person.NameMaxLength, c => c.Person.FirstName, (c, v) => c.Person.FirstName = v!, required: true),
        Field.Text<Contact>("lastName", Person.NameMaxLength, c => c.Person.LastName, (c, v) => c.Person.LastName = v!, required: true),
        Field.Reference<Contact, Company>("companyId", "Firma bulunamadı.", c => c.CompanyId, (c, v) => c.CompanyId = v),
        Field.Text<Contact>("title", Person.TitleMaxLength, c => c.Person.Title, (c, v) => c.Person.Title = v),
        Field.Email<Contact>("email", Person.EmailMaxLength, c => c.Person.Email, (c, v) => c.Person.Email = v),
        Field.Text<Contact>("phone", Person.PhoneMaxLength, c => c.Person.Phone, (c, v) => c.Person.Phone = v),
        Field.Text<Contact>("notes", Company.NotesMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Contact>> Filters { get; } = new Dictionary<string, RecordFilter<Contact>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.CompanyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PersonId == id) : null,
    };

    protected override IQueryable<Contact> Load(IQueryable<Contact> source) => source.Include(c => c.Person);

    protected override IOrderedQueryable<Contact> Order(IQueryable<Contact> source) =>
        source.OrderBy(c => c.Person.LastName).ThenBy(c => c.Person.FirstName);

    protected override IQueryable<Contact> Search(IQueryable<Contact> source, string term) =>
        source.Where(c => c.Person.FirstName.Contains(term)
            || c.Person.LastName.Contains(term)
            || (c.Person.Email != null && c.Person.Email.Contains(term)));

    /// <summary>
    /// Yeni kişi "personId" verildiyse o ortak kişiye bağlanır (kişinin zaten bir CRM kişisi varsa doğrulama hatası), yoksa
    /// yeni ortak kişi açılır. E-posta kesin eşleşme sayılmaz; aynı adresli iki kişi olabilir.
    /// </summary>
    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Contact entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (FieldNamed("personId").Parse(values["personId"]).Value is not Guid personId)
        {
            entity.AttachPerson(new Person());
            return (values, false);
        }

        var person = await Context.Data.Set<Person>().SingleOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new RecordValidationException([new RecordFieldError("personId", "Kişi bulunamadı.")]);
        if (await Context.Data.Set<Contact>().AnyAsync(c => c.PersonId == person.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("personId", "Bu kişi CRM'de zaten kayıtlı.")]);
        }

        entity.AttachPerson(person);
        return (values, true);
    }

    /// <summary>Kişinin çalıştığı taraf, CRM'deki firmasının tarafıdır; firma verildiğinde (veya kaldırıldığında) eşitlenir.</summary>
    protected override async Task AppliedAsync(Contact entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (!values.ContainsKey("companyId"))
        {
            return;
        }

        entity.Person.PartyId = entity.CompanyId is { } companyId
            ? await Context.Data.Set<Company>().Where(c => c.Id == companyId).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken)
            : null;
    }

    protected override SharedEntity SharedOf(Contact entity) => entity.Person;

    protected override JsonObject SharedState(Contact entity) => new()
    {
        ["firstName"] = entity.Person.FirstName, ["lastName"] = entity.Person.LastName, ["title"] = entity.Person.Title,
        ["email"] = entity.Person.Email, ["phone"] = entity.Person.Phone, ["partyId"] = entity.Person.PartyId?.ToString(),
    };
}

internal sealed class OpportunityRecordStore(CrmStoreContext context) : CrmRecordStore<Opportunity>(context)
{
    public override string EntityKey => CrmModule.Entities.Opportunity;

    protected override string ReadPermission => CrmModule.Permissions.OpportunitiesRead;

    protected override string WritePermission => CrmModule.Permissions.OpportunitiesWrite;

    protected override IReadOnlyList<RecordField<Opportunity>> Fields { get; } =
    [
        Field.Text<Opportunity>("title", Opportunity.TitleMaxLength, o => o.Title, (o, v) => o.Title = v!, required: true),
        Field.Reference<Opportunity, Company>("companyId", "Firma bulunamadı.", o => o.CompanyId, (o, v) => o.CompanyId = v!.Value, required: true),
        Field.Reference<Opportunity, Contact>("contactId", "Kişi bulunamadı.", o => o.ContactId, (o, v) => o.ContactId = v),
        Field.ListChoice<Opportunity>("stage", CrmModule.Lists.OpportunityStage, o => o.Stage, (o, v) => o.Stage = v!),
        Field.Money<Opportunity>("amount", o => o.Amount, (o, v) => o.Amount = v),
        Field.Currency<Opportunity>("currency", o => o.Currency, (o, v) => o.Currency = v),
        Field.Date<Opportunity>("expectedCloseDate", o => o.ExpectedCloseDate, (o, v) => o.ExpectedCloseDate = v),
        Field.Percent<Opportunity>("probability", o => o.Probability, (o, v) => o.Probability = v),
        Field.Text<Opportunity>("lostReason", Opportunity.LostReasonMaxLength, o => o.LostReason, (o, v) => o.LostReason = v),
        Field.Owner<Opportunity>(OwnerField, o => o.OwnerId, (o, v) => o.OwnerId = v),
        Field.Text<Opportunity>("notes", Company.NotesMaxLength, o => o.Notes, (o, v) => o.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Opportunity>> Filters { get; } = new Dictionary<string, RecordFilter<Opportunity>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.CompanyId == id) : null,
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(o => o.ContactId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(o => o.OwnerId == owner) : null,
    };

    /// <summary>
    /// "open=true": kazanılmamış ve kaybedilmemiş fırsatlar. Kapalı aşamalar firmanın listesindeki kategoriden gelir (Y9b):
    /// firmanın eklediği "Kazanıldı" kategorili aşama da fırsatı kapatır.
    /// </summary>
    protected override IReadOnlyDictionary<string, AsyncRecordFilter<Opportunity>> AsyncFilters { get; } =
        new Dictionary<string, AsyncRecordFilter<Opportunity>>
        {
            ["stage"] = async (q, v, ctx, ct) =>
                await FilterValue.ChoiceAsync(v, CrmModule.Lists.OpportunityStage, ctx, ct) is { } stage ? q.Where(o => o.Stage == stage) : null,
            ["open"] = async (q, v, ctx, ct) =>
            {
                if (FilterValue.Bool(v) is not { } open)
                {
                    return null;
                }

                var stages = await ctx.ChoiceLists.GetAsync(CrmModule.Lists.OpportunityStage, ct);
                var closed = stages.KeysIn(CrmModule.StageCategories.Won).Concat(stages.KeysIn(CrmModule.StageCategories.Lost)).ToList();
                return open ? q.Where(o => !closed.Contains(o.Stage)) : q.Where(o => closed.Contains(o.Stage));
            },
        };

    protected override IOrderedQueryable<Opportunity> Order(IQueryable<Opportunity> source) =>
        source.OrderByDescending(o => o.CreatedAt);

    protected override IQueryable<Opportunity> Search(IQueryable<Opportunity> source, string term) =>
        source.Where(o => o.Title.Contains(term));
}

internal sealed class ActivityRecordStore(CrmStoreContext context) : CrmRecordStore<Activity>(context)
{
    public override string EntityKey => CrmModule.Entities.Activity;

    protected override string ReadPermission => CrmModule.Permissions.ActivitiesRead;

    protected override string WritePermission => CrmModule.Permissions.ActivitiesWrite;

    protected override IReadOnlyList<RecordField<Activity>> Fields { get; } =
    [
        Field.ListChoice<Activity>("type", CrmModule.Lists.ActivityType, a => a.Type, (a, v) => a.Type = v!),
        Field.Text<Activity>("subject", Activity.SubjectMaxLength, a => a.Subject, (a, v) => a.Subject = v!, required: true),
        Field.Time<Activity>("dueAt", a => a.DueAt, (a, v) => a.DueAt = v),
        Field.Time<Activity>("completedAt", a => a.CompletedAt, (a, v) => a.CompletedAt = v),
        Field.Reference<Activity, Company>("companyId", "Firma bulunamadı.", a => a.CompanyId, (a, v) => a.CompanyId = v),
        Field.Reference<Activity, Contact>("contactId", "Kişi bulunamadı.", a => a.ContactId, (a, v) => a.ContactId = v),
        Field.Reference<Activity, Opportunity>("opportunityId", "Fırsat bulunamadı.", a => a.OpportunityId, (a, v) => a.OpportunityId = v),
        Field.Owner<Activity>(OwnerField, a => a.OwnerId, (a, v) => a.OwnerId = v),
        Field.Text<Activity>("notes", Company.NotesMaxLength, a => a.Notes, (a, v) => a.Notes = v),
    ];

    /// <summary>
    /// "open=true": tamamlanmamış. "dueBefore": bu zamandan önce yapılması gereken (ör. bugünün sonu veya şimdi);
    /// gün sınırını kullanıcının saat dilimine göre istemci hesaplar.
    /// </summary>
    protected override IReadOnlyDictionary<string, RecordFilter<Activity>> Filters { get; } = new Dictionary<string, RecordFilter<Activity>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.CompanyId == id) : null,
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.ContactId == id) : null,
        ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.OpportunityId == id) : null,
        ["open"] = (q, v, _) => FilterValue.Bool(v) is { } open ? q.Where(a => (a.CompletedAt == null) == open) : null,
        ["dueBefore"] = (q, v, _) => FilterValue.Time(v) is { } time ? q.Where(a => a.DueAt != null && a.DueAt < time) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(a => a.OwnerId == owner) : null,
    };

    /// <summary>Açık aktiviteler önce, en yakın tarihli üstte; tarihsizler sonda.</summary>
    protected override IOrderedQueryable<Activity> Order(IQueryable<Activity> source) =>
        source.OrderBy(a => a.CompletedAt != null).ThenBy(a => a.DueAt == null).ThenBy(a => a.DueAt);

    protected override IQueryable<Activity> Search(IQueryable<Activity> source, string term) =>
        source.Where(a => a.Subject.Contains(term));
}
