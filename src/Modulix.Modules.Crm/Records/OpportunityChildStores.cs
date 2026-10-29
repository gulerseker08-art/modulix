using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Fırsat alt kayıtlarının ortak kuralları: görünürlük fırsatın veri kapsamına bağlıdır.</summary>
internal static class OpportunityLinks
{
    /// <summary>Fırsatı çağıran görebilir mi (fırsat okuma izni ve veri kapsamı)? Erişilemeyen fırsat, olmayanla aynı cevabı alır.</summary>
    public static async Task<bool> CanSeeAsync(CrmStoreContext context, Guid opportunityId, CancellationToken cancellationToken)
    {
        var owner = await context.Data.Set<Opportunity>().AsNoTracking()
            .Where(o => o.Id == opportunityId).Select(o => new { o.OwnerId }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null || !await context.Access.CanAsync(CrmModule.Entities.Opportunity, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var scope = await context.Access.GetOwnersAsync(cancellationToken);
        return scope is null || (owner.OwnerId is { } ownerId && scope.Contains(ownerId));
    }

    public static async Task<bool> InScopeAsync(CrmStoreContext context, RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (record.Values["opportunityId"]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var opportunityId))
        {
            return false;
        }

        var owner = await context.Data.Set<Opportunity>().AsNoTracking()
            .Where(o => o.Id == opportunityId).Select(o => o.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return owner is { } ownerId && owners.Contains(ownerId);
    }
}

/// <summary>
/// Fırsat–kişi ilişkileri (ADR-0017): "crm.opportunity-contact". Karar verici, bütçe sahibi gibi roller kişi kaydında değil bu
/// ilişkidedir. Okuma ve yazma fırsat izinleriyle olur; ilişki fırsatın veri kapsamındadır; fırsat ve kişiye erişim gerekir.
/// </summary>
internal sealed class OpportunityContactRecordStore(CrmStoreContext context) : CrmRecordStore<OpportunityContact>(context)
{
    public override string EntityKey => CrmModule.Entities.OpportunityContact;

    protected override string ReadPermission => CrmModule.Permissions.OpportunitiesRead;

    protected override string WritePermission => CrmModule.Permissions.OpportunitiesWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<OpportunityContact>> Fields { get; } =
    [
        Field.SharedId<OpportunityContact>("opportunityId", l => l.OpportunityId),
        Field.SharedId<OpportunityContact>("contactId", l => l.ContactId),
        Field.ReadOnly<OpportunityContact>("contactName", l => l.Contact?.Person is { } p ? $"{p.FirstName} {p.LastName}" : null),
        Field.KeyList<OpportunityContact>("roles", CrmModule.OpportunityContactRoles, OpportunityContact.RolesMaxLength, l => l.RoleList, (l, v) => l.Roles = v),
        Field.Flag<OpportunityContact>("isPrimary", l => l.IsPrimary, (l, v) => l.IsPrimary = v),
        Field.Text<OpportunityContact>("note", OpportunityContact.NoteMaxLength, l => l.Note, (l, v) => l.Note = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OpportunityContact>> Filters { get; } =
        new Dictionary<string, RecordFilter<OpportunityContact>>
        {
            ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.OpportunityId == id) : null,
            ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ContactId == id) : null,
        };

    protected override IQueryable<OpportunityContact> Load(IQueryable<OpportunityContact> source) =>
        source.Include(l => l.Contact).ThenInclude(c => c.Person);

    protected override IOrderedQueryable<OpportunityContact> Order(IQueryable<OpportunityContact> source) =>
        source.OrderByDescending(l => l.IsPrimary).ThenBy(l => l.Contact.Person.LastName).ThenBy(l => l.Contact.Person.FirstName);

    protected override IQueryable<OpportunityContact> Search(IQueryable<OpportunityContact> source, string term) =>
        source.Where(l => l.Contact.Person.FirstName.Contains(term) || l.Contact.Person.LastName.Contains(term));

    protected override IQueryable<OpportunityContact> ScopeToOwners(IQueryable<OpportunityContact> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var opportunities = Context.Data.Set<Opportunity>();
        return source.Where(l => opportunities.Any(o => o.Id == l.OpportunityId && ids.Contains(o.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        OpportunityLinks.InScopeAsync(Context, record, owners, cancellationToken);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(
        OpportunityContact entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var opportunityId = FieldNamed("opportunityId").Parse(values["opportunityId"]).Value as Guid?;
        var contactId = FieldNamed("contactId").Parse(values["contactId"]).Value as Guid?;
        if (opportunityId is null)
        {
            errors.Add(new RecordFieldError("opportunityId", "Bu alan zorunludur."));
        }
        else if (!await OpportunityLinks.CanSeeAsync(Context, opportunityId.Value, cancellationToken))
        {
            errors.Add(new RecordFieldError("opportunityId", "Fırsat bulunamadı."));
        }

        if (contactId is null)
        {
            errors.Add(new RecordFieldError("contactId", "Bu alan zorunludur."));
        }
        else if (!await CompanyLinks.CanSeeContactAsync(Context, contactId.Value, cancellationToken))
        {
            errors.Add(new RecordFieldError("contactId", "Kişi bulunamadı."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.Contact = await Context.Data.Set<Contact>().Include(c => c.Person).SingleAsync(c => c.Id == contactId, cancellationToken);
        entity.OpportunityId = opportunityId!.Value;
        entity.ContactId = contactId!.Value;
        return (values, false);
    }

    protected override async Task AppliedAsync(OpportunityContact entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var data = Context.Data.Set<OpportunityContact>();
        if (await data.AnyAsync(l => l.OpportunityId == entity.OpportunityId && l.ContactId == entity.ContactId && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("contactId", "Bu kişi bu fırsata zaten bağlı."));
        }

        // Ana irtibat fırsat başına tektir; değiştirmek için önce eskisinin işareti kaldırılır.
        if (entity.IsPrimary && await data.AnyAsync(l => l.OpportunityId == entity.OpportunityId && l.IsPrimary && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("isPrimary", "Bu fırsatta zaten bir ana irtibat var; önce onun işaretini kaldırın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }
}

/// <summary>
/// Fırsat rakipleri (ADR-0017): "crm.opportunity-competitor". Rakip bir firma kaydı olmak zorunda değildir.
/// </summary>
internal sealed class OpportunityCompetitorRecordStore(CrmStoreContext context) : CrmRecordStore<OpportunityCompetitor>(context)
{
    public override string EntityKey => CrmModule.Entities.OpportunityCompetitor;

    protected override string ReadPermission => CrmModule.Permissions.OpportunitiesRead;

    protected override string WritePermission => CrmModule.Permissions.OpportunitiesWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<OpportunityCompetitor>> Fields { get; } =
    [
        Field.SharedId<OpportunityCompetitor>("opportunityId", c => c.OpportunityId),
        Field.Text<OpportunityCompetitor>("name", Opportunity.ShortMaxLength, c => c.Name, (c, v) => c.Name = v!, required: true),
        Field.Text<OpportunityCompetitor>("strengths", Opportunity.TextMaxLength, c => c.Strengths, (c, v) => c.Strengths = v),
        Field.Text<OpportunityCompetitor>("weaknesses", Opportunity.TextMaxLength, c => c.Weaknesses, (c, v) => c.Weaknesses = v),
        Field.Money<OpportunityCompetitor>("alternativeAmount", c => c.AlternativeAmount, (c, v) => c.AlternativeAmount = v),
        Field.Text<OpportunityCompetitor>("alternativeCurrency", Opportunity.CurrencyLength, c => c.AlternativeCurrency, (c, v) => c.AlternativeCurrency = v?.ToUpperInvariant()),
        Field.Text<OpportunityCompetitor>("notes", Opportunity.TextMaxLength, c => c.Notes, (c, v) => c.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<OpportunityCompetitor>> Filters { get; } =
        new Dictionary<string, RecordFilter<OpportunityCompetitor>>
        {
            ["opportunityId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(c => c.OpportunityId == id) : null,
        };

    protected override IOrderedQueryable<OpportunityCompetitor> Order(IQueryable<OpportunityCompetitor> source) =>
        source.OrderBy(c => c.Name);

    protected override IQueryable<OpportunityCompetitor> Search(IQueryable<OpportunityCompetitor> source, string term) =>
        source.Where(c => c.Name.Contains(term));

    protected override IQueryable<OpportunityCompetitor> ScopeToOwners(IQueryable<OpportunityCompetitor> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var opportunities = Context.Data.Set<Opportunity>();
        return source.Where(c => opportunities.Any(o => o.Id == c.OpportunityId && ids.Contains(o.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        OpportunityLinks.InScopeAsync(Context, record, owners, cancellationToken);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(
        OpportunityCompetitor entity, JsonObject values, CancellationToken cancellationToken)
    {
        var opportunityId = FieldNamed("opportunityId").Parse(values["opportunityId"]).Value as Guid?;
        if (opportunityId is null)
        {
            throw new RecordValidationException([new RecordFieldError("opportunityId", "Bu alan zorunludur.")]);
        }

        if (!await OpportunityLinks.CanSeeAsync(Context, opportunityId.Value, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("opportunityId", "Fırsat bulunamadı.")]);
        }

        entity.OpportunityId = opportunityId.Value;
        return (values, false);
    }

    protected override Task AppliedAsync(OpportunityCompetitor entity, JsonObject values, CancellationToken cancellationToken)
    {
        if (entity.AlternativeCurrency is { } code && (code.Length != Opportunity.CurrencyLength || !code.All(char.IsAsciiLetter)))
        {
            throw new RecordValidationException([new RecordFieldError("alternativeCurrency", "Üç harfli para birimi kodu girin (ör. TRY).")]);
        }

        if (entity.AlternativeAmount is not null && entity.AlternativeCurrency is null)
        {
            throw new RecordValidationException([new RecordFieldError("alternativeCurrency", "Alternatif teklif tutarı için para birimi girin.")]);
        }

        return Task.CompletedTask;
    }
}
