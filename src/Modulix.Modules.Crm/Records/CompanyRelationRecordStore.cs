using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Firma–firma ilişkileri (ADR-0012): ana firma → bağlı firma. İlişki iki ucu da çağıranın kapsamındaysa görünür.
/// Kendine bağ ve döngü reddedilir; döngü kontrolünün eşzamanlı yarışı, ilgili firmaların sürümünü artırarak
/// (iyimser eşzamanlılık) kapatılır: aynı firmalara dokunan ikinci yazım 409 alır.
/// </summary>
internal sealed class CompanyRelationRecordStore(CrmStoreContext context) : CrmRecordStore<CompanyRelation>(context)
{
    private const int MaxDepth = 100;

    public override string EntityKey => CrmModule.Entities.CompanyRelation;

    protected override string ReadPermission => CrmModule.Permissions.CompaniesRead;

    protected override string WritePermission => CrmModule.Permissions.CompaniesWrite;

    protected override bool UsesScope => true;

    protected override string ScopeEntityKey => CrmModule.Entities.Company;

    protected override IReadOnlyList<RecordField<CompanyRelation>> Fields { get; } =
    [
        Field.CreateOnlyId<CompanyRelation>("parentCompanyId", r => r.ParentCompanyId),
        Field.CreateOnlyId<CompanyRelation>("childCompanyId", r => r.ChildCompanyId),
        Field.ReadOnly<CompanyRelation>("parentCompanyName", r => r.ParentCompany?.Party?.Name),
        Field.ReadOnly<CompanyRelation>("childCompanyName", r => r.ChildCompany?.Party?.Name),
        Field.Choice<CompanyRelation>("type", CrmModule.RelationTypes, r => r.Type, (r, v) => r.Type = v),
        Field.Date<CompanyRelation>("startDate", r => r.StartDate, (r, v) => { if (v is { } start) { r.StartDate = start; } }),
        Field.Date<CompanyRelation>("endDate", r => r.EndDate, (r, v) => r.EndDate = v),
        Field.Flag<CompanyRelation>("isActive", r => r.IsActive, (r, v) => r.IsActive = v, createOnly: true),
        Field.Text<CompanyRelation>("notes", CompanyRelation.NotesMaxLength, r => r.Notes, (r, v) => r.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<CompanyRelation>> Filters { get; } =
        new Dictionary<string, RecordFilter<CompanyRelation>>
        {
            ["companyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(r => r.ParentCompanyId == id || r.ChildCompanyId == id) : null,
            ["parentCompanyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(r => r.ParentCompanyId == id) : null,
            ["childCompanyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(r => r.ChildCompanyId == id) : null,
            ["active"] = (q, v, _) => FilterValue.Bool(v) is { } active ? q.Where(r => r.IsActive == active) : null,
        };

    protected override IQueryable<CompanyRelation> Load(IQueryable<CompanyRelation> source) =>
        source.Include(r => r.ParentCompany).ThenInclude(c => c.Party).Include(r => r.ChildCompany).ThenInclude(c => c.Party);

    protected override IOrderedQueryable<CompanyRelation> Order(IQueryable<CompanyRelation> source) =>
        source.OrderByDescending(r => r.IsActive).ThenBy(r => r.ParentCompany.Party.Name).ThenBy(r => r.ChildCompany.Party.Name);

    protected override IQueryable<CompanyRelation> Search(IQueryable<CompanyRelation> source, string term) =>
        source.Where(r => r.ParentCompany.Party.Name.Contains(term) || r.ChildCompany.Party.Name.Contains(term));

    protected override IQueryable<CompanyRelation> ScopeToOwners(IQueryable<CompanyRelation> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var companies = Context.Data.Set<Company>();
        return source.Where(r => companies.Any(c => c.Id == r.ParentCompanyId && ids.Contains(c.OwnerId))
            && companies.Any(c => c.Id == r.ChildCompanyId && ids.Contains(c.OwnerId)));
    }

    protected override async Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        await CompanyScope.InScopeAsync(Context, record, "parentCompanyId", owners, cancellationToken)
        && await CompanyScope.InScopeAsync(Context, record, "childCompanyId", owners, cancellationToken);

    protected override async Task<(JsonObject Values, bool Linked)> PrepareNewAsync(CompanyRelation entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var parentId = CompanyScope.IdOf(values["parentCompanyId"]);
        var childId = CompanyScope.IdOf(values["childCompanyId"]);
        foreach (var (name, id) in new[] { ("parentCompanyId", parentId), ("childCompanyId", childId) })
        {
            if (id is null)
            {
                errors.Add(new RecordFieldError(name, "Bu alan zorunludur."));
            }
            else if (!await CompanyLinks.CanSeeCompanyAsync(Context, id.Value, cancellationToken))
            {
                errors.Add(new RecordFieldError(name, "Firma bulunamadı."));
            }
        }

        if (errors.Count == 0 && parentId == childId)
        {
            errors.Add(new RecordFieldError("childCompanyId", "Bir firma kendisine bağlanamaz."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        entity.ParentCompanyId = parentId!.Value;
        entity.ChildCompanyId = childId!.Value;
        entity.ParentCompany = await Context.Data.Set<Company>().Include(c => c.Party).SingleAsync(c => c.Id == parentId, cancellationToken);
        entity.ChildCompany = await Context.Data.Set<Company>().Include(c => c.Party).SingleAsync(c => c.Id == childId, cancellationToken);
        return (values, false);
    }

    protected override async Task AppliedAsync(CompanyRelation entity, JsonObject values, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        entity.IsActive = entity.EndDate is null;
        if (!entity.IsActive && entity.EndDate < entity.StartDate)
        {
            errors.Add(new RecordFieldError("endDate", "Bitiş tarihi başlangıçtan önce olamaz."));
        }

        HashSet<Guid>? group = null;
        if (entity.IsActive)
        {
            if (await Context.Data.Set<CompanyRelation>().AnyAsync(r => r.IsActive && r.Id != entity.Id
                    && r.ParentCompanyId == entity.ParentCompanyId && r.ChildCompanyId == entity.ChildCompanyId, cancellationToken))
            {
                errors.Add(new RecordFieldError("childCompanyId", "Bu iki firma arasında zaten aktif bir ilişki var."));
            }
            else
            {
                // Döngü: bağlı firmanın altındakiler arasında ana firma varsa bu bağ halkayı kapatır.
                var descendants = await ReachAsync(entity.ChildCompanyId, entity.Id, down: true, cancellationToken);
                if (descendants.Contains(entity.ParentCompanyId))
                {
                    errors.Add(new RecordFieldError("childCompanyId", "Bu bağ firma hiyerarşisinde döngü oluşturur."));
                }
                else
                {
                    var ancestors = await ReachAsync(entity.ParentCompanyId, entity.Id, down: false, cancellationToken);
                    group = [.. descendants, .. ancestors, entity.ParentCompanyId, entity.ChildCompanyId];
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        // Eşzamanlı iki bağın birlikte döngü kurmasını önler: ilgili firmaların sürümü artar, aynı firmalara dokunan ikinci yazım 409 alır.
        if (group is not null)
        {
            foreach (var company in await Context.Data.Set<Company>().Where(c => group.Contains(c.Id)).ToListAsync(cancellationToken))
            {
                company.Touch();
            }
        }
    }

    /// <summary>Verilen firmadan aktif ilişkilerle aşağı (bağlı firmalar) ya da yukarı (ana firmalar) ulaşılan tüm firmalar.</summary>
    private async Task<HashSet<Guid>> ReachAsync(Guid start, Guid exceptRelation, bool down, CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid>();
        var frontier = new List<Guid> { start };
        var relations = Context.Data.Set<CompanyRelation>().AsNoTracking().Where(r => r.IsActive && r.Id != exceptRelation);
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var batch = frontier;
            var next = down
                ? await relations.Where(r => batch.Contains(r.ParentCompanyId)).Select(r => r.ChildCompanyId).ToListAsync(cancellationToken)
                : await relations.Where(r => batch.Contains(r.ChildCompanyId)).Select(r => r.ParentCompanyId).ToListAsync(cancellationToken);
            frontier = next.Where(seen.Add).ToList();
        }

        return seen;
    }
}
