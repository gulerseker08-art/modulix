using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Kayıt türleri: "crm.company", "crm.contact", "crm.opportunity", "crm.activity".
// Alan ve filtre adları API'de ve audit kaydında görünen teknik kimliklerdir; değiştirmeyin.
// Sorumlu filtresi "ownerId=me" isteği yapan kullanıcıyı seçer.
// Firma ve kişi ortak kaydın uzantısıdır (ADR-0006): ad, vergi no, iletişim gibi alanlar API'de aynı adlarla durur ama
// ortak kayda (Party, Person) yazılır; aynı kaydı diğer modüller de kullanır.

internal sealed partial class CompanyRecordStore(CrmStoreContext context) : CrmRecordStore<Company>(context), IMergeableRecordStore
{
    /// <summary>Taraftaki alanlar. Mevcut tarafa bağlanırken boş gönderilenler taraftaki değeri silmez.</summary>
    private static readonly string[] PartyFields = ["name", "kind", "taxOffice", "taxNumber", "phone", "email", "website", "city", "address"];

    public override string EntityKey => CrmModule.Entities.Company;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override IReadOnlyList<RecordField<Company>> Fields { get; } =
    [
        Field.SharedId<Company>("partyId", c => c.PartyId),
        Field.Text<Company>("name", Party.NameMaxLength, c => c.Party.Name, (c, v) => c.Party.Name = v!, required: true),
        Field.Kind<Company>("kind", c => c.Party.Kind, (c, v) => c.Party.Kind = v),
        Field.Text<Company>("taxOffice", Party.TaxOfficeMaxLength, c => c.Party.TaxOffice, (c, v) => { if (v is not null) c.Party.TaxOffice = v; }),
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
        // Kesin eşleşme (mükerrer önerisi): vergi numarası. İletişim eşleşmesi yalnızca paylaşılmayan (santral/ortak adres olmayan) noktalarla.
        ["taxNumber"] = (q, v, _) => v.Trim() is { Length: > 0 and <= Party.TaxNumberMaxLength } number ? q.Where(c => c.Party.TaxNumber == number) : null,
        ["contactValue"] = (q, v, ctx) => ContactNormalization.IdentityValue(v) is { } value
            ? q.Where(c => ctx.Data.Set<ContactPoint>().Any(p => p.CompanyId == c.Id && p.IsActive && !p.IsShared && p.NormalizedValue == value))
            : null,
        // Şehir hem eski şehir alanında hem aktif adreslerde aranır (ADR-0012).
        ["city"] = (q, v, ctx) => q.Where(c => c.Party.City == v || ctx.Data.Set<Address>().Any(a => a.CompanyId == c.Id && a.IsActive && a.City == v)),
        ["kind"] = (q, v, _) => Enum.TryParse<PartyKind>(v, ignoreCase: false, out var kind) && Enum.IsDefined(kind) && !char.IsDigit(v[0])
            ? q.Where(c => c.Party.Kind == kind)
            : null,
    };

    protected override IReadOnlyDictionary<string, AsyncRecordFilter<Company>> AsyncFilters { get; } =
        new Dictionary<string, AsyncRecordFilter<Company>>
        {
            ["status"] = async (q, v, ctx, ct) =>
                await FilterValue.ChoiceAsync(v, CrmModule.Lists.CompanyStatus, ctx, ct) is { } status ? q.Where(c => c.Status == status) : null,
        };

    protected override IQueryable<Company> Load(IQueryable<Company> source) => source.Include(c => c.Party);

    protected override IOrderedQueryable<Company> Order(IQueryable<Company> source) => source.OrderBy(c => c.Party.Name);

    // Ad, vergi no, şehir ve etiketin yanında tüm iletişim noktalarında (ikinci telefon, lokasyon e-postası...) ve adreslerde aranır;
    // telefon ve adres normalleştirilmiş değerle eşleşir (ADR-0012).
    protected override IQueryable<Company> Search(IQueryable<Company> source, string term)
    {
        var folded = ContactNormalization.FoldText(term);
        var lower = term.Trim().ToLowerInvariant();
        var phone = ContactNormalization.NormalizePhone(term);
        var points = Context.Data.Set<ContactPoint>();
        var addresses = Context.Data.Set<Address>();
        return source.Where(c => c.Party.Name.Contains(term)
            || (c.Party.TaxNumber != null && c.Party.TaxNumber.Contains(term))
            || (c.Party.City != null && c.Party.City.Contains(term))
            || (c.Tags != null && c.Tags.Contains(term))
            || points.Any(p => p.CompanyId == c.Id && (p.NormalizedValue.Contains(lower) || (phone != null && p.NormalizedValue == phone)))
            || (folded != "" && addresses.Any(a => a.CompanyId == c.Id && a.NormalizedText.Contains(folded))));
    }

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
        await EnsureTaxNumberFormatAsync(entity, cancellationToken);
        if (entity.Party.TaxNumber is { } number
            && await Context.Data.Set<Party>().AnyAsync(p => p.TaxNumber == number && p.Id != entity.PartyId, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("taxNumber", "Bu vergi numarasıyla kayıtlı bir firma var.")]);
        }

        // Eski telefon/e-posta/şehir/adres alanları yapılandırılmış ana kayıtlara yansır (ADR-0012).
        await LegacyContactSync.FromCompanyAsync(Context.Data, entity, cancellationToken);
    }

    /// <summary>
    /// Tüzel kişide vergi numarası 10, gerçek kişide T.C. kimlik numarası 11 hanelidir (ADR-0006). Yalnızca numara ya da tür
    /// değiştiğinde denetlenir: eskiden girilmiş serbest biçimli numara, başka alan düzenlenirken hata vermez.
    /// </summary>
    private async Task EnsureTaxNumberFormatAsync(Company entity, CancellationToken cancellationToken)
    {
        if (entity.Party.TaxNumber is not { } number)
        {
            return;
        }

        var stored = await Context.Data.Set<Party>().AsNoTracking()
            .Where(p => p.Id == entity.PartyId).Select(p => new { p.TaxNumber, p.Kind }).SingleOrDefaultAsync(cancellationToken);
        if (stored is not null && stored.TaxNumber == number && stored.Kind == entity.Party.Kind)
        {
            return;
        }

        var length = entity.Party.Kind == PartyKind.Individual ? 11 : 10;
        if (number.Length != length || !number.All(char.IsAsciiDigit))
        {
            throw new RecordValidationException([new RecordFieldError("taxNumber",
                entity.Party.Kind == PartyKind.Individual ? "T.C. kimlik numarası 11 haneli olmalıdır." : "Vergi numarası 10 haneli olmalıdır.")]);
        }
    }

    /// <summary>
    /// Firma silinirken kendi adresleri ve iletişim noktaları gider; lokasyonu veya firma ilişkisi olan firma silinmez (ADR-0012),
    /// önce onlar kaldırılır.
    /// </summary>
    protected override async Task BeforeDeleteAsync(Company entity, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        if (await data.Set<Location>().AnyAsync(l => l.CompanyId == entity.Id, cancellationToken)
            || await data.Set<CompanyRelation>().AnyAsync(r => r.ParentCompanyId == entity.Id || r.ChildCompanyId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Firmanın lokasyonları veya firma ilişkileri var; önce onları kaldırın.");
        }

        data.Set<Address>().RemoveRange(await data.Set<Address>().Where(a => a.CompanyId == entity.Id).ToListAsync(cancellationToken));
        data.Set<ContactPoint>().RemoveRange(await data.Set<ContactPoint>().Where(p => p.CompanyId == entity.Id).ToListAsync(cancellationToken));
    }

    protected override SharedEntity SharedOf(Company entity) => entity.Party;

    protected override JsonObject SharedState(Company entity) => new()
    {
        ["name"] = entity.Party.Name, ["kind"] = entity.Party.Kind.ToString(), ["taxOffice"] = entity.Party.TaxOffice, ["taxNumber"] = entity.Party.TaxNumber, ["phone"] = entity.Party.Phone,
        ["email"] = entity.Party.Email, ["website"] = entity.Party.Website, ["city"] = entity.Party.City, ["address"] = entity.Party.Address,
    };
}

internal sealed partial class ContactRecordStore(CrmStoreContext context) : CrmRecordStore<Contact>(context), IMergeableRecordStore
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
        Field.Owner<Contact>(OwnerField, c => c.OwnerId, (c, v) => c.OwnerId = v),
        Field.Text<Contact>("notes", Company.NotesMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Contact>> Filters { get; } = new Dictionary<string, RecordFilter<Contact>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.CompanyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.PersonId == id) : null,
        [OwnerField] = (q, v, ctx) => FilterValue.User(v, ctx) is { } owner ? q.Where(c => c.OwnerId == owner) : null,
        ["withoutCompany"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(c => (c.CompanyId == null) == flag) : null,
        // Mükerrer önerisi: paylaşılmayan aktif iletişim noktasında aynı e-posta/telefon.
        ["contactValue"] = (q, v, ctx) => ContactNormalization.IdentityValue(v) is { } value
            ? q.Where(c => ctx.Data.Set<ContactPoint>().Any(p => p.ContactId == c.Id && p.IsActive && !p.IsShared && p.NormalizedValue == value))
            : null,
        // Aktif ilişkisinde bu ilişki rolü olan kişiler (ADR-0011).
        ["role"] = (q, v, ctx) => CrmModule.RelationshipRoles.Contains(v)
            ? q.Where(c => ctx.Data.Set<CompanyContact>().Any(l => l.ContactId == c.Id && l.IsActive && l.Roles != null && l.Roles.Contains(v)))
            : null,
    };

    /// <summary>
    /// Kişi görünürlüğü (ADR-0010): firmaya bağlı kişi firmanın sorumlusuna göre, firmasız kişi kendi sorumlusuna göre
    /// kapsamdadır. Tüm kayıtlar kapsamında (<c>owners</c> null) herkes görünür; bu ifade yalnızca kısıtlı kapsam içindir.
    /// </summary>
    internal static Expression<Func<Contact, bool>> VisibleTo(IModuleData data, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var companies = data.Set<Company>();
        var links = data.Set<CompanyContact>();
        return c => links.Any(l => l.ContactId == c.Id && l.IsActive)
            ? links.Any(l => l.ContactId == c.Id && l.IsActive && companies.Any(co => co.Id == l.CompanyId && ids.Contains(co.OwnerId)))
            : ids.Contains(c.OwnerId);
    }

    protected override IQueryable<Contact> ScopeToOwners(IQueryable<Contact> source, IReadOnlySet<Guid> owners) =>
        source.Where(VisibleTo(Context.Data, owners));

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        // Kayıttaki ilişkiler (kayıtlı hal): bağlı firmalardan biri kapsamdaysa görünür.
        if (await Context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == record.Id).Where(VisibleTo(Context.Data, owners))
                .AnyAsync(cancellationToken))
        {
            return true;
        }

        // Henüz kaydedilmemiş yazma (oluşturma/güncelleme sonrası denetim): birincil firma ya da sorumlu.
        if (record.Values["companyId"] is JsonValue company && company.TryGetValue<string>(out var text) && Guid.TryParse(text, out var companyId))
        {
            var owner = await Context.Data.Set<Company>().AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
            return owner is { } ownerId && owners.Contains(ownerId);
        }

        return await base.IsInScopeAsync(record, owners, cancellationToken);
    }

    protected override IQueryable<Contact> Load(IQueryable<Contact> source) => source.Include(c => c.Person);

    protected override IOrderedQueryable<Contact> Order(IQueryable<Contact> source) =>
        source.OrderBy(c => c.Person.LastName).ThenBy(c => c.Person.FirstName);

    // Ad, soyad ve tüm iletişim noktalarında (ikinci e-posta, ilişkiye özel telefon...) aranır; telefon normalleştirilmiş değerle eşleşir.
    protected override IQueryable<Contact> Search(IQueryable<Contact> source, string term)
    {
        var lower = term.Trim().ToLowerInvariant();
        var phone = ContactNormalization.NormalizePhone(term);
        var points = Context.Data.Set<ContactPoint>();
        return source.Where(c => c.Person.FirstName.Contains(term)
            || c.Person.LastName.Contains(term)
            || (c.Person.Email != null && c.Person.Email.Contains(term))
            || points.Any(p => p.ContactId == c.Id && (p.NormalizedValue.Contains(lower) || (phone != null && p.NormalizedValue == phone))));
    }

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
        // Eski telefon/e-posta alanları kişinin genel ana noktalarına yansır (ADR-0012).
        await LegacyContactSync.FromContactAsync(Context.Data, entity, cancellationToken);

        // Eski istemciler sorumluyu göndermez (boş); kişi sorumlusuz kalıp kapsamdan düşmesin diye mevcut sorumlu korunur.
        if (entity.OwnerId is null)
        {
            entity.OwnerId = await Context.Data.Set<Contact>().AsNoTracking()
                .Where(c => c.Id == entity.Id).Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
        }

        if (!values.ContainsKey("companyId"))
        {
            return;
        }

        await EnsureCompanyAccessAsync(entity, cancellationToken);
        await SyncLinksAsync(entity, cancellationToken);
        entity.Person.PartyId = entity.CompanyId is { } companyId
            ? await Context.Data.Set<Company>().Where(c => c.Id == companyId).Select(c => (Guid?)c.PartyId).SingleOrDefaultAsync(cancellationToken)
            : null;
    }

    /// <summary>
    /// Eski API'nin tek firma alanı ilişkilere çevrilir (ADR-0011): önceki birincil firmayla ilişki sonlanır, yeni firmayla
    /// aktif ilişki yoksa açılır; <c>CompanyId</c> yeniden birincil firmaya (en eski aktif ilişki) eşitlenir. Alan değişmediyse
    /// hiçbir şey yapılmaz, böylece ekranın tüm kaydı yeniden göndermesi çok firmalı kişinin ilişkilerini bozmaz.
    /// </summary>
    private async Task SyncLinksAsync(Contact entity, CancellationToken cancellationToken)
    {
        var previous = await Context.Data.Set<Contact>().AsNoTracking()
            .Where(c => c.Id == entity.Id).Select(c => c.CompanyId).SingleOrDefaultAsync(cancellationToken);
        var desired = entity.CompanyId;
        if (previous == desired)
        {
            return;
        }

        var links = Context.Data.Set<CompanyContact>();
        var ended = new List<Guid>();
        if (previous is { } oldCompany)
        {
            foreach (var link in await links.Where(l => l.ContactId == entity.Id && l.CompanyId == oldCompany && l.IsActive).ToListAsync(cancellationToken))
            {
                link.EndDate = CompanyLinks.Today;
                link.IsActive = false;
                link.IsPrimary = false;
                link.Touch();
                ended.Add(link.Id);
            }
        }

        var extra = new List<(Guid, DateOnly, DateTimeOffset)>();
        if (desired is { } newCompany && !await links.AnyAsync(l => l.ContactId == entity.Id && l.CompanyId == newCompany && l.IsActive, cancellationToken))
        {
            var added = new CompanyContact
            {
                CompanyId = newCompany, ContactId = entity.Id, Contact = entity, JobTitle = entity.Person.Title, StartDate = CompanyLinks.Today,
            };
            links.Add(added);
            extra.Add((newCompany, added.StartDate, added.CreatedAt));
        }
        else if (desired is { } existing)
        {
            // Zaten aktif ilişkisi olan firma: mevcut ilişki korunur ve sıralamada onun başlangıcı geçerlidir.
            var start = await links.Where(l => l.ContactId == entity.Id && l.CompanyId == existing && l.IsActive)
                .Select(l => new { l.StartDate, l.CreatedAt }).FirstAsync(cancellationToken);
            extra.Add((existing, start.StartDate, start.CreatedAt));
        }

        entity.CompanyId = await CompanyLinks.PrimaryCompanyAsync(Context.Data, entity.Id, ended, extra, cancellationToken);
    }

    protected override async Task BeforeDeleteAsync(Contact entity, CancellationToken cancellationToken)
    {
        // Kişi silinince ilişkileri de gider (kişiyi silmek bilinçli bir işlemdir; geçmişi korumak için ilişki sonlandırılır).
        Context.Data.Set<ContactPoint>().RemoveRange(
            await Context.Data.Set<ContactPoint>().Where(p => p.ContactId == entity.Id).ToListAsync(cancellationToken));
        Context.Data.Set<CompanyContact>().RemoveRange(
            await Context.Data.Set<CompanyContact>().Where(l => l.ContactId == entity.Id).ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Kişiyi bir firmaya bağlamak için firmayı görme yetkisi gerekir (ADR-0010): firma okuma izni ve firmanın veri kapsamı.
    /// Erişilemeyen firma, var olmayan firmayla aynı cevabı alır. Firma değişmediyse (ör. ekran kaydı yeniden gönderirken)
    /// denetlenmez.
    /// </summary>
    private async Task EnsureCompanyAccessAsync(Contact entity, CancellationToken cancellationToken)
    {
        if (entity.CompanyId is not { } companyId)
        {
            return;
        }

        var stored = await Context.Data.Set<Contact>().AsNoTracking()
            .Where(c => c.Id == entity.Id).Select(c => c.CompanyId).SingleOrDefaultAsync(cancellationToken);
        if (stored == companyId)
        {
            return;
        }

        var owner = await Context.Data.Set<Company>().AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
        var scope = await Context.Access.GetOwnersAsync(cancellationToken);
        var visible = await Context.Access.CanAsync(CrmModule.Entities.Company, RecordOperation.Read, cancellationToken)
            && (scope is null || (owner is { } ownerId && scope.Contains(ownerId)));
        if (!visible)
        {
            throw new RecordValidationException([new RecordFieldError("companyId", "Firma bulunamadı.")]);
        }
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
            // "stageCategory=won|lost|open": aşamanın kategorisine göre (firmanın eklediği aşamalar dahil).
            ["stageCategory"] = async (q, v, ctx, ct) =>
            {
                var category = v.Trim().ToLowerInvariant();
                if (category is not (CrmModule.StageCategories.Open or CrmModule.StageCategories.Won or CrmModule.StageCategories.Lost))
                {
                    return null;
                }

                var keys = (await ctx.ChoiceLists.GetAsync(CrmModule.Lists.OpportunityStage, ct)).KeysIn(category).ToList();
                return q.Where(o => keys.Contains(o.Stage));
            },
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
