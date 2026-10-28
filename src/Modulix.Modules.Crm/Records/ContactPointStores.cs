using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Telefon/e-posta noktalarının ortak kuralları (ADR-0012); sahibe göre iki depoya ayrılır (firma/lokasyon ve kişi).</summary>
internal abstract class ContactPointRecordStore(CrmStoreContext context) : CrmRecordStore<ContactPoint>(context)
{
    protected static readonly RecordField<ContactPoint>[] CommonFields =
    [
        Field.Choice<ContactPoint>("channel", CrmModule.ContactChannels, p => p.Channel, (p, v) => p.Channel = v),
        Field.Choice<ContactPoint>("type", CrmModule.ContactPointTypes, p => p.Type, (p, v) => p.Type = v),
        Field.Text<ContactPoint>("value", ContactPoint.ValueMaxLength, p => p.Value, (p, v) => p.Value = v!, required: true),
        Field.ReadOnly<ContactPoint>("normalizedValue", p => p.NormalizedValue),
        Field.Flag<ContactPoint>("isPrimary", p => p.IsPrimary, (p, v) => p.IsPrimary = v),
        Field.Flag<ContactPoint>("isActive", p => p.IsActive, (p, v) => p.IsActive = v),
        Field.Flag<ContactPoint>("isShared", p => p.IsShared, (p, v) => p.IsShared = v),
        Field.Text<ContactPoint>("note", ContactPoint.NoteMaxLength, p => p.Note, (p, v) => p.Note = v),
    ];

    protected override IOrderedQueryable<ContactPoint> Order(IQueryable<ContactPoint> source) =>
        source.OrderByDescending(p => p.IsActive).ThenByDescending(p => p.IsPrimary).ThenBy(p => p.Channel).ThenBy(p => p.Value);

    // Arama normalleştirilmiş değerde de yapılır: "0532 123 45 67" yazan "+905321234567"yi bulur.
    protected override IQueryable<ContactPoint> Search(IQueryable<ContactPoint> source, string term)
    {
        var phone = ContactNormalization.NormalizePhone(term);
        var folded = term.Trim().ToLowerInvariant();
        return source.Where(p => p.Value.Contains(term) || p.NormalizedValue.Contains(folded) || (phone != null && p.NormalizedValue == phone));
    }

    /// <summary>Aynı sahibin (ve kişide aynı ilişki kapsamının) noktaları.</summary>
    protected abstract IQueryable<ContactPoint> SameOwner(IQueryable<ContactPoint> source, ContactPoint point);

    protected abstract Task MirrorAsync(ContactPoint point, bool wasPrimary, CancellationToken cancellationToken);

    protected override async Task AppliedAsync(ContactPoint entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var normalized = entity.Channel == ContactNormalization.Phone
            ? ContactNormalization.NormalizePhone(entity.Value)
            : ContactNormalization.NormalizeEmail(entity.Value);
        if (normalized is null)
        {
            errors.Add(new RecordFieldError("value", entity.Channel == ContactNormalization.Phone
                ? "Geçerli bir telefon numarası girin (7-15 rakam; dahili numara nota yazılır)."
                : "Geçerli bir e-posta adresi girin."));
        }
        else if (entity.Channel == ContactNormalization.Phone && entity.Value.Length > Party.PhoneMaxLength)
        {
            errors.Add(new RecordFieldError("value", $"Telefon en fazla {Party.PhoneMaxLength} karakter olabilir."));
        }

        if (!entity.IsActive)
        {
            entity.IsPrimary = false;
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.NormalizedValue = normalized!;

        // Yalnızca aynı sahipte tekrar ve ikinci ana nokta engellenir: aynı e-posta/santral başka kişi veya firmalarda da bulunabilir.
        var siblings = SameOwner(Context.Data.Set<ContactPoint>(), entity).Where(p => p.Id != entity.Id && p.Channel == entity.Channel && p.IsActive);
        if (entity.IsActive && await siblings.AnyAsync(p => p.NormalizedValue == normalized, cancellationToken))
        {
            errors.Add(new RecordFieldError("value", "Bu iletişim noktası aynı sahipte zaten kayıtlı."));
        }

        if (entity.IsPrimary && await siblings.AnyAsync(p => p.IsPrimary, cancellationToken))
        {
            errors.Add(new RecordFieldError("isPrimary", "Bu sahipte bu kanal için zaten bir ana kayıt var; önce onun ana işaretini kaldırın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        var wasPrimary = await Context.Data.Set<ContactPoint>().AsNoTracking()
            .AnyAsync(p => p.Id == entity.Id && p.IsPrimary && p.IsActive, cancellationToken);
        await MirrorAsync(entity, wasPrimary, cancellationToken);
    }

    protected override async Task BeforeDeleteAsync(ContactPoint entity, CancellationToken cancellationToken)
    {
        if (entity.IsPrimary && entity.IsActive)
        {
            entity.IsPrimary = false;
            await MirrorAsync(entity, wasPrimary: true, cancellationToken);
        }
    }

    /// <summary>Kanal oluştururken yazılır (create-only alan yazımı atlanır); verilmezse telefon kalır.</summary>
    protected static void ReadChannel(ContactPoint entity, JsonObject values)
    {
        if (values["channel"] is JsonValue node && node.TryGetValue<string>(out var channel) && CrmModule.ContactChannels.Contains(channel.Trim()))
        {
            entity.Channel = channel.Trim();
        }
    }
}

/// <summary>Firma ve lokasyon sahipli iletişim noktaları ("crm.contact-point"); firma izinleriyle ve firmanın kapsamıyla.</summary>
internal sealed class CompanyContactPointRecordStore(CrmStoreContext context) : ContactPointRecordStore(context)
{
    public override string EntityKey => CrmModule.Entities.ContactPoint;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<ContactPoint>> Fields { get; } =
    [
        Field.CreateOnlyId<ContactPoint>("companyId", p => p.CompanyId),
        Field.CreateOnlyId<ContactPoint>("locationId", p => p.LocationId),
        .. CommonFields,
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContactPoint>> Filters { get; } = new Dictionary<string, RecordFilter<ContactPoint>>
    {
        ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.CompanyId == id) : null,
        ["locationId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.LocationId == id) : null,
        ["companyLevel"] = (q, v, _) => FilterValue.Bool(v) is { } flag ? q.Where(p => (p.LocationId == null) == flag) : null,
        ["channel"] = (q, v, _) => CrmModule.ContactChannels.Contains(v) ? q.Where(p => p.Channel == v) : null,
        ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(p => p.IsActive == active) : null,
    };

    // Bu depo yalnızca firma/lokasyon sahipli noktaları görür; kişi sahiplileri kişi izinleriyle ayrı depodadır.
    protected override IQueryable<ContactPoint> Load(IQueryable<ContactPoint> source) => source.Where(p => p.CompanyId != null);

    protected override IQueryable<ContactPoint> ScopeToOwners(IQueryable<ContactPoint> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var companies = Context.Data.Set<Company>();
        return source.Where(p => companies.Any(c => c.Id == p.CompanyId && ids.Contains(c.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        CompanyScope.InScopeAsync(Context, record, "companyId", owners, cancellationToken);

    protected override IQueryable<ContactPoint> SameOwner(IQueryable<ContactPoint> source, ContactPoint point) =>
        point.LocationId is { } location
            ? source.Where(p => p.LocationId == location)
            : source.Where(p => p.CompanyId == point.CompanyId && p.LocationId == null);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(ContactPoint entity, JsonObject values, CancellationToken cancellationToken)
    {
        var company = await CompanyScope.RequireCompanyAsync(Context, "companyId", values, cancellationToken);
        entity.CompanyId = company.Id;
        entity.LocationId = CompanyScope.IdOf(values["locationId"]);
        if (entity.LocationId is { } locationId
            && !await Context.Data.Set<Location>().AnyAsync(l => l.Id == locationId && l.CompanyId == company.Id, cancellationToken))
        {
            throw CompanyScope.Invalid("locationId", "Lokasyon bu firmaya ait değil.");
        }

        ReadChannel(entity, values);
        return (values, false);
    }

    protected override async Task MirrorAsync(ContactPoint point, bool wasPrimary, CancellationToken cancellationToken)
    {
        if (point.LocationId is null && point.CompanyId is { } companyId)
        {
            LegacyContactSync.MirrorPoint(point, wasPrimary, await CompanyScope.PartyOfAsync(Context, companyId, cancellationToken), null);
        }
    }
}

/// <summary>
/// Kişi sahipli iletişim noktaları ("crm.person-contact-point"); kişi izinleriyle ve kişinin görünürlüğüyle (ADR-0010).
/// İsteğe bağlı olarak bir firma–kişi ilişkisine özeldir (<c>companyContactId</c>).
/// </summary>
internal sealed class PersonContactPointRecordStore(CrmStoreContext context) : ContactPointRecordStore(context)
{
    public override string EntityKey => CrmModule.Entities.PersonContactPoint;

    protected override string ReadPermission => CrmModule.Permissions.ContactsRead;

    protected override string WritePermission => CrmModule.Permissions.ContactsWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<ContactPoint>> Fields { get; } =
    [
        Field.CreateOnlyId<ContactPoint>("contactId", p => p.ContactId),
        Field.CreateOnlyId<ContactPoint>("companyContactId", p => p.CompanyContactId),
        .. CommonFields,
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContactPoint>> Filters { get; } = new Dictionary<string, RecordFilter<ContactPoint>>
    {
        ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.ContactId == id) : null,
        ["companyContactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.CompanyContactId == id) : null,
        ["channel"] = (q, v, _) => CrmModule.ContactChannels.Contains(v) ? q.Where(p => p.Channel == v) : null,
        ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(p => p.IsActive == active) : null,
    };

    protected override IQueryable<ContactPoint> Load(IQueryable<ContactPoint> source) => source.Where(p => p.ContactId != null);

    protected override IQueryable<ContactPoint> ScopeToOwners(IQueryable<ContactPoint> source, IReadOnlySet<Guid> owners)
    {
        var visible = Context.Data.Set<Contact>().Where(ContactRecordStore.VisibleTo(Context.Data, owners));
        return source.Where(p => visible.Any(c => c.Id == p.ContactId));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        CompanyScope.IdOf(record.Values["contactId"]) is { } contactId
        && await Context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == contactId)
            .Where(ContactRecordStore.VisibleTo(Context.Data, owners)).AnyAsync(cancellationToken);

    protected override IQueryable<ContactPoint> SameOwner(IQueryable<ContactPoint> source, ContactPoint point) =>
        source.Where(p => p.ContactId == point.ContactId && p.CompanyContactId == point.CompanyContactId);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(ContactPoint entity, JsonObject values, CancellationToken cancellationToken)
    {
        var contactId = CompanyScope.IdOf(values["contactId"]) ?? throw CompanyScope.Invalid("contactId", "Bu alan zorunludur.");
        if (!await CompanyLinks.CanSeeContactAsync(Context, contactId, cancellationToken))
        {
            throw CompanyScope.Invalid("contactId", "Kişi bulunamadı.");
        }

        if (CompanyScope.IdOf(values["companyContactId"]) is { } linkId)
        {
            var link = await Context.Data.Set<CompanyContact>().AsNoTracking()
                .Where(l => l.Id == linkId && l.ContactId == contactId).Select(l => new { l.CompanyId }).SingleOrDefaultAsync(cancellationToken);
            if (link is null || !await CompanyLinks.CanSeeCompanyAsync(Context, link.CompanyId, cancellationToken))
            {
                throw CompanyScope.Invalid("companyContactId", "İlişki bu kişiye ait değil.");
            }
        }

        entity.ContactId = contactId;
        entity.CompanyContactId = CompanyScope.IdOf(values["companyContactId"]);
        ReadChannel(entity, values);
        return (values, false);
    }

    protected override async Task MirrorAsync(ContactPoint point, bool wasPrimary, CancellationToken cancellationToken)
    {
        if (point.CompanyContactId is null && point.ContactId is { } contactId)
        {
            var person = await Context.Data.Set<Contact>().Where(c => c.Id == contactId).Select(c => c.Person).SingleAsync(cancellationToken);
            LegacyContactSync.MirrorPoint(point, wasPrimary, null, person);
        }
    }
}
