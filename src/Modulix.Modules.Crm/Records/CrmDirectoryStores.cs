using System.Linq.Expressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

// Firma altı kayıtlar (ADR-0012): lokasyonlar, adresler, iletişim noktaları ve firma–firma ilişkileri.
// Hepsi bağlı oldukları firmanın izinlerine ve veri kapsamına tabidir (kişi sahipli noktalar kişinin kapsamına).

/// <summary>Firma altı kayıtların ortak kapsam ve erişim yardımcıları.</summary>
internal static class CompanyScope
{
    /// <summary>Kaydı, firması kapsamdaki sorumlulardan birine ait olanlarla sınırlar (sorguda; sayfalama doğru kalır).</summary>
    public static IQueryable<T> ByCompany<T>(
        CrmStoreContext context, IQueryable<T> source, IReadOnlySet<Guid> owners, Expression<Func<T, Guid>> companyId)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var visible = context.Data.Set<Company>().Where(c => ids.Contains(c.OwnerId)).Select(c => c.Id);
        var contains = Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [typeof(Guid)], visible.Expression, companyId.Body);
        return source.Where(Expression.Lambda<Func<T, bool>>(contains, companyId.Parameters[0]));
    }

    public static async Task<bool> InScopeAsync(
        CrmStoreContext context, RecordData record, string field, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (IdOf(record.Values[field]) is not { } companyId)
        {
            return false;
        }

        var owner = await context.Data.Set<Company>().AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return owner is { } ownerId && owners.Contains(ownerId);
    }

    public static RecordValidationException Invalid(string field, string message) => new([new RecordFieldError(field, message)]);

    public static Guid? IdOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Firmayı görüp görmediği denetlenir ve ortak kaydıyla yüklenir; erişilemeyen firma "bulunamadı" sayılır.</summary>
    public static async Task<Company> RequireCompanyAsync(CrmStoreContext context, string field, JsonObject values, CancellationToken cancellationToken)
    {
        var companyId = IdOf(values[field]) ?? throw Invalid(field, "Bu alan zorunludur.");
        if (!await CompanyLinks.CanSeeCompanyAsync(context, companyId, cancellationToken))
        {
            throw Invalid(field, "Firma bulunamadı.");
        }

        return await context.Data.Set<Company>().Include(c => c.Party).SingleAsync(c => c.Id == companyId, cancellationToken);
    }

    public static Task<Party> PartyOfAsync(CrmStoreContext context, Guid companyId, CancellationToken cancellationToken) =>
        context.Data.Set<Company>().Where(c => c.Id == companyId).Select(c => c.Party).SingleAsync(cancellationToken);
}

internal sealed class LocationRecordStore(CrmStoreContext context) : CrmRecordStore<Location>(context)
{
    public override string EntityKey => CrmModule.Entities.Location;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<Location>> Fields { get; } =
    [
        Field.CreateOnlyId<Location>("companyId", l => l.CompanyId),
        Field.ReadOnly<Location>("companyName", l => l.Company?.Party?.Name),
        Field.Text<Location>("name", Location.NameMaxLength, l => l.Name, (l, v) => l.Name = v!, required: true),
        Field.Text<Location>("code", Location.CodeMaxLength, l => l.Code, (l, v) => l.Code = v),
        Field.Choice<Location>("type", CrmModule.LocationTypes, l => l.Type, (l, v) => l.Type = v),
        Field.Flag<Location>("isActive", l => l.IsActive, (l, v) => l.IsActive = v),
        Field.Reference<Location, Contact>("contactId", "Kişi bulunamadı.", l => l.ContactId, (l, v) => l.ContactId = v),
        Field.Text<Location>("notes", Company.NotesMaxLength, l => l.Notes, (l, v) => l.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Location>> Filters { get; } = new Dictionary<string, RecordFilter<Location>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.CompanyId == id) : null,
        ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(l => l.IsActive == active) : null,
        ["type"] = (q, v, _) => CrmModule.LocationTypes.Contains(v) ? q.Where(l => l.Type == v) : null,
    };

    protected override IQueryable<Location> Load(IQueryable<Location> source) => source.Include(l => l.Company).ThenInclude(c => c.Party);

    protected override IOrderedQueryable<Location> Order(IQueryable<Location> source) =>
        source.OrderByDescending(l => l.IsActive).ThenBy(l => l.Name);

    protected override IQueryable<Location> Search(IQueryable<Location> source, string term) =>
        source.Where(l => l.Name.Contains(term) || (l.Code != null && l.Code.Contains(term)));

    protected override IQueryable<Location> ScopeToOwners(IQueryable<Location> source, IReadOnlySet<Guid> owners) =>
        CompanyScope.ByCompany(Context, source, owners, l => l.CompanyId);

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        CompanyScope.InScopeAsync(Context, record, "companyId", owners, cancellationToken);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Location entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.Company = await CompanyScope.RequireCompanyAsync(Context, "companyId", values, cancellationToken);
        entity.CompanyId = entity.Company.Id;
        return (values, false);
    }

    protected override async Task AppliedAsync(Location entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var locations = Context.Data.Set<Location>();
        entity.Code = string.IsNullOrWhiteSpace(entity.Code) ? null : entity.Code.Trim();
        if (entity.Code is { } code
            && await locations.AnyAsync(l => l.CompanyId == entity.CompanyId && l.Code == code && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu kod firmanın başka bir lokasyonunda kullanılıyor."));
        }

        if (entity.ContactId is { } contactId)
        {
            var stored = await locations.AsNoTracking().Where(l => l.Id == entity.Id).Select(l => l.ContactId).SingleOrDefaultAsync(cancellationToken);
            if (stored != contactId && !await CompanyLinks.CanSeeContactAsync(Context, contactId, cancellationToken))
            {
                errors.Add(new RecordFieldError("contactId", "Kişi bulunamadı."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(Location entity, CancellationToken cancellationToken)
    {
        // Lokasyonun adresleri ve iletişim noktaları onunla birlikte gider; başka kayıtlar lokasyona bağlı değildir.
        Context.Data.Set<Address>().RemoveRange(await Context.Data.Set<Address>().Where(a => a.LocationId == entity.Id).ToListAsync(cancellationToken));
        Context.Data.Set<ContactPoint>().RemoveRange(await Context.Data.Set<ContactPoint>().Where(p => p.LocationId == entity.Id).ToListAsync(cancellationToken));
    }
}

internal sealed class AddressRecordStore(CrmStoreContext context) : CrmRecordStore<Address>(context)
{
    public override string EntityKey => CrmModule.Entities.Address;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<Address>> Fields { get; } =
    [
        Field.CreateOnlyId<Address>("companyId", a => a.CompanyId),
        Field.CreateOnlyId<Address>("locationId", a => a.LocationId),
        Field.Choice<Address>("type", CrmModule.AddressTypes, a => a.Type, (a, v) => a.Type = v),
        Field.Flag<Address>("isPrimary", a => a.IsPrimary, (a, v) => a.IsPrimary = v),
        Field.Flag<Address>("isActive", a => a.IsActive, (a, v) => a.IsActive = v),
        Field.Text<Address>("country", Address.CountryLength, a => a.Country, (a, v) => { if (v is not null) a.Country = v.ToUpperInvariant(); }),
        Field.Text<Address>("city", Address.PartMaxLength, a => a.City, (a, v) => a.City = v),
        Field.Text<Address>("district", Address.PartMaxLength, a => a.District, (a, v) => a.District = v),
        Field.Text<Address>("postalCode", Address.PostalCodeMaxLength, a => a.PostalCode, (a, v) => a.PostalCode = v),
        Field.Text<Address>("line1", Address.LineMaxLength, a => a.Line1, (a, v) => a.Line1 = v!, required: true),
        Field.Text<Address>("line2", Address.LineMaxLength, a => a.Line2, (a, v) => a.Line2 = v),
        Field.ReadOnly<Address>("normalizedText", a => a.NormalizedText),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Address>> Filters { get; } = new Dictionary<string, RecordFilter<Address>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.CompanyId == id) : null,
        ["locationId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(a => a.LocationId == id) : null,
        ["companyLevel"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(a => (a.LocationId == null) == flag) : null,
        ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(a => a.IsActive == active) : null,
        ["city"] = (q, v, _) => q.Where(a => a.City == v),
    };

    protected override IQueryable<Address> Load(IQueryable<Address> source) => source.Include(a => a.Company).ThenInclude(c => c.Party);

    protected override IOrderedQueryable<Address> Order(IQueryable<Address> source) =>
        source.OrderByDescending(a => a.IsActive).ThenByDescending(a => a.IsPrimary).ThenBy(a => a.Type);

    // Arama normalleştirilmiş metinde yapılır: "Şişli" yazan "sisli" bulur.
    protected override IQueryable<Address> Search(IQueryable<Address> source, string term)
    {
        var folded = ContactNormalization.FoldText(term);
        return source.Where(a => a.NormalizedText.Contains(folded));
    }

    protected override IQueryable<Address> ScopeToOwners(IQueryable<Address> source, IReadOnlySet<Guid> owners) =>
        CompanyScope.ByCompany(Context, source, owners, a => a.CompanyId);

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        CompanyScope.InScopeAsync(Context, record, "companyId", owners, cancellationToken);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(Address entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.Company = await CompanyScope.RequireCompanyAsync(Context, "companyId", values, cancellationToken);
        entity.CompanyId = entity.Company.Id;
        entity.LocationId = CompanyScope.IdOf(values["locationId"]);
        if (entity.LocationId is { } locationId
            && !await Context.Data.Set<Location>().AnyAsync(l => l.Id == locationId && l.CompanyId == entity.CompanyId, cancellationToken))
        {
            throw CompanyScope.Invalid("locationId", "Lokasyon bu firmaya ait değil.");
        }

        return (values, false);
    }

    protected override async Task AppliedAsync(Address entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (entity.Country.Length != Address.CountryLength || !entity.Country.All(char.IsAsciiLetter))
        {
            errors.Add(new RecordFieldError("country", "İki harfli ülke kodu girin (ör. TR)."));
        }

        if (!entity.IsActive)
        {
            entity.IsPrimary = false;
        }

        var addresses = Context.Data.Set<Address>();
        if (entity.IsPrimary && await addresses.AnyAsync(
                a => a.CompanyId == entity.CompanyId && a.LocationId == entity.LocationId && a.IsPrimary && a.IsActive && a.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("isPrimary", "Bu sahipte zaten bir ana adres var; önce onun ana adres işaretini kaldırın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.NormalizedText = LegacyContactSync.Normalized(entity);
        if (entity.LocationId is null)
        {
            var wasPrimary = await addresses.AsNoTracking().AnyAsync(a => a.Id == entity.Id && a.IsPrimary && a.IsActive, cancellationToken);
            LegacyContactSync.MirrorAddress(entity, wasPrimary, await CompanyScope.PartyOfAsync(Context, entity.CompanyId, cancellationToken));
        }
    }

    protected override async Task BeforeDeleteAsync(Address entity, CancellationToken cancellationToken)
    {
        // Silinen ana adres eski Party.Address/City aynasını da boşaltır.
        if (entity.LocationId is null && entity.IsPrimary && entity.IsActive)
        {
            var party = await CompanyScope.PartyOfAsync(Context, entity.CompanyId, cancellationToken);
            entity.IsPrimary = false;
            LegacyContactSync.MirrorAddress(entity, wasPrimary: true, party);
        }
    }
}
