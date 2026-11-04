using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Firma–kişi ilişkilerinin ortak kuralları (ADR-0011).</summary>
internal static class CompanyLinks
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Kişinin "birincil firması" (eski <c>Contact.CompanyId</c>): en eski başlayan aktif ilişkinin firması. Henüz
    /// kaydedilmemiş değişiklikler çağıranca verilir: <paramref name="except"/> sayılmaz, <paramref name="extra"/> eklenir.
    /// </summary>
    public static async Task<Guid?> PrimaryCompanyAsync(
        IModuleData data,
        Guid contactId,
        IReadOnlyCollection<Guid> except,
        IReadOnlyList<(Guid CompanyId, DateOnly Start, DateTimeOffset Created)> extra,
        CancellationToken cancellationToken)
    {
        var stored = await data.Set<CompanyContact>().AsNoTracking()
            .Where(l => l.ContactId == contactId && l.IsActive && !except.Contains(l.Id))
            .Select(l => new { l.CompanyId, l.StartDate, l.CreatedAt })
            .ToListAsync(cancellationToken);
        return stored.Select(l => (l.CompanyId, l.StartDate, l.CreatedAt)).Concat(extra)
            .OrderBy(l => l.Item2).ThenBy(l => l.Item3).Select(l => (Guid?)l.Item1).FirstOrDefault();
    }

    /// <summary>
    /// Firmayı çağıran görebilir mi: firma okuma izni ve veri kapsamı. Erişilemeyen firma, olmayan firmayla aynı cevabı alır.
    /// </summary>
    public static async Task<bool> CanSeeCompanyAsync(CrmStoreContext context, Guid companyId, CancellationToken cancellationToken)
    {
        var owner = await context.Data.Set<Company>().AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => new { c.OwnerId }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null || !await context.Access.CanAsync(CrmModule.Entities.Company, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var scope = await context.Access.GetOwnersAsync(CrmModule.Entities.Company, cancellationToken);
        return scope is null || (owner.OwnerId is { } ownerId && scope.Contains(ownerId));
    }

    /// <summary>Kişiyi çağıran görebilir mi (kişi okuma izni ve ADR-0010 görünürlüğü)?</summary>
    public static async Task<bool> CanSeeContactAsync(CrmStoreContext context, Guid contactId, CancellationToken cancellationToken)
    {
        if (!await context.Access.CanAsync(CrmModule.Entities.Contact, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var query = context.Data.Set<Contact>().AsNoTracking().Where(c => c.Id == contactId);
        if (await context.Access.GetOwnersAsync(CrmModule.Entities.Contact, cancellationToken) is { } scope)
        {
            query = query.Where(ContactRecordStore.VisibleTo(context.Data, scope));
        }

        return await query.AnyAsync(cancellationToken);
    }
}

/// <summary>
/// Firma–kişi ilişkileri (ADR-0011): "crm.company-contact". Okuma ve yazma kişi izinleriyle olur; ilişki, bağlı olduğu
/// firmanın veri kapsamına göre görünür. Firma ve kişi yalnızca oluştururken verilir ve ikisine de erişim gerekir.
/// </summary>
internal sealed class CompanyContactRecordStore(CrmStoreContext context) : CrmRecordStore<CompanyContact>(context)
{
    public override string EntityKey => CrmModule.Entities.CompanyContact;

    protected override string ReadPermission => CrmModule.Permissions.ContactsRead;

    protected override string WritePermission => CrmModule.Permissions.ContactsWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => CrmModule.Entities.Company;

    protected override IReadOnlyList<RecordField<CompanyContact>> Fields { get; } =
    [
        Field.SharedId<CompanyContact>("companyId", l => l.CompanyId),
        Field.SharedId<CompanyContact>("contactId", l => l.ContactId),
        Field.ReadOnly<CompanyContact>("companyName", l => l.Company?.Party?.Name),
        Field.ReadOnly<CompanyContact>("contactName", l => l.Contact?.Person is { } p ? $"{p.FirstName} {p.LastName}" : null),
        Field.Text<CompanyContact>("department", CompanyContact.DepartmentMaxLength, l => l.Department, (l, v) => l.Department = v),
        Field.Text<CompanyContact>("jobTitle", CompanyContact.JobTitleMaxLength, l => l.JobTitle, (l, v) => l.JobTitle = v),
        Field.KeyList<CompanyContact>("roles", CrmModule.RelationshipRoles, CompanyContact.RolesMaxLength, l => l.RoleList, (l, v) => l.Roles = v),
        Field.Flag<CompanyContact>("isPrimary", l => l.IsPrimary, (l, v) => l.IsPrimary = v),
        Field.Flag<CompanyContact>("isDecisionMaker", l => l.IsDecisionMaker, (l, v) => l.IsDecisionMaker = v),
        Field.Date<CompanyContact>("startDate", l => l.StartDate, (l, v) => { if (v is { } start) { l.StartDate = start; } }),
        Field.Date<CompanyContact>("endDate", l => l.EndDate, (l, v) => l.EndDate = v),
        Field.Flag<CompanyContact>("isActive", l => l.IsActive, (l, v) => l.IsActive = v, createOnly: true),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CompanyContact>> Filters { get; } =
        new Dictionary<string, RecordFilter<CompanyContact>>
        {
            ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.CompanyId == id) : null,
            ["contactId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ContactId == id) : null,
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(l => l.IsActive == active) : null,
        };

    protected override IQueryable<CompanyContact> Load(IQueryable<CompanyContact> source) =>
        source.Include(l => l.Company).ThenInclude(c => c.Party).Include(l => l.Contact).ThenInclude(c => c.Person);

    protected override IOrderedQueryable<CompanyContact> Order(IQueryable<CompanyContact> source) =>
        source.OrderByDescending(l => l.IsActive).ThenByDescending(l => l.IsPrimary)
            .ThenBy(l => l.Contact.Person.LastName).ThenBy(l => l.Contact.Person.FirstName);

    protected override IQueryable<CompanyContact> Search(IQueryable<CompanyContact> source, string term) =>
        source.Where(l => l.Contact.Person.FirstName.Contains(term) || l.Contact.Person.LastName.Contains(term)
            || (l.JobTitle != null && l.JobTitle.Contains(term)) || (l.Department != null && l.Department.Contains(term)));

    // İlişki, firmasının veri kapsamındadır: kişinin diğer firmalarla ilişkileri bu firmayı görmeyene gösterilmez.
    protected override IQueryable<CompanyContact> ScopeToOwners(IQueryable<CompanyContact> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var companies = Context.Data.Set<Company>();
        return source.Where(l => companies.Any(c => c.Id == l.CompanyId && ids.Contains(c.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken)
    {
        if (record.Values["companyId"]?.GetValue<string>() is not { } text || !Guid.TryParse(text, out var companyId))
        {
            return false;
        }

        var owner = await Context.Data.Set<Company>().AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.OwnerId).SingleOrDefaultAsync(cancellationToken);
        return owner is { } ownerId && owners.Contains(ownerId);
    }

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(
        CompanyContact entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var companyId = FieldNamed("companyId").Parse(values["companyId"]).Value as Guid?;
        var contactId = FieldNamed("contactId").Parse(values["contactId"]).Value as Guid?;
        if (companyId is null)
        {
            errors.Add(new RecordFieldError("companyId", "Bu alan zorunludur."));
        }
        else if (!await CompanyLinks.CanSeeCompanyAsync(Context, companyId.Value, cancellationToken))
        {
            errors.Add(new RecordFieldError("companyId", "Firma bulunamadı."));
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

        entity.Company = await Context.Data.Set<Company>().Include(c => c.Party).SingleAsync(c => c.Id == companyId, cancellationToken);
        entity.Contact = await Context.Data.Set<Contact>().Include(c => c.Person).SingleAsync(c => c.Id == contactId, cancellationToken);
        entity.CompanyId = entity.Company.Id;
        entity.ContactId = entity.Contact.Id;
        return (values, false);
    }

    protected override async Task AppliedAsync(CompanyContact entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        // Aktiflik bitiş tarihinden türetilir; biten ilişki ana irtibat olamaz.
        entity.IsActive = entity.EndDate is null;
        if (!entity.IsActive)
        {
            entity.IsPrimary = false;
            if (entity.EndDate < entity.StartDate)
            {
                errors.Add(new RecordFieldError("endDate", "Bitiş tarihi başlangıçtan önce olamaz."));
            }
        }

        var data = Context.Data.Set<CompanyContact>();
        if (entity.IsActive && await data.AnyAsync(
                l => l.CompanyId == entity.CompanyId && l.ContactId == entity.ContactId && l.IsActive && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("contactId", "Bu kişi bu firmayla zaten ilişkili."));
        }

        // Ana irtibat firma başına tektir (yalnızca aktif ilişkiler); değiştirmek için önce eskisinin işareti kaldırılır.
        if (entity.IsPrimary && await data.AnyAsync(
                l => l.CompanyId == entity.CompanyId && l.IsPrimary && l.IsActive && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("isPrimary", "Bu firmada zaten bir ana irtibat var; önce onun ana irtibat işaretini kaldırın."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        // Eski API'nin birincil firması bu kayıtlardan türetilir (tek doğruluk kaynağı ilişkilerdir).
        var primary = await CompanyLinks.PrimaryCompanyAsync(
            Context.Data, entity.ContactId, [entity.Id],
            entity.IsActive ? [(entity.CompanyId, entity.StartDate, entity.CreatedAt)] : [], cancellationToken);
        if (entity.Contact.CompanyId != primary)
        {
            entity.Contact.CompanyId = primary;
            entity.Contact.Person.PartyId = primary is { } companyId
                ? await Context.Data.Set<Company>().Where(c => c.Id == companyId).Select(c => (Guid?)c.PartyId).SingleAsync(cancellationToken)
                : null;
            entity.Contact.Touch();
            entity.Contact.Person.Touch();
        }
    }
}
