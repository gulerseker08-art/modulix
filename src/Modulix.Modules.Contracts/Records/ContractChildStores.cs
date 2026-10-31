using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Contracts.Domain;
using Modulix.Modules.Contracts.Lifecycle;

namespace Modulix.Modules.Contracts.Records;

/// <summary>
/// Sözleşme tarafları ve ilgili kişileri. Görünürlük ve veri kapsamı üst sözleşmeyi izler; sözleşme içerik düzenlemesine kapalıysa
/// taraf eklenemez, değiştirilemez, silinemez. Taraf ya da kişiden en az biri verilir; ikisi de verildiyse kişi tarafa bağlı olmalıdır.
/// </summary>
internal sealed class ContractPartyRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractParty>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractParty;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.ContractsWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<ContractParty>> Fields { get; } =
    [
        Field.Reference<ContractParty, Contract>("contractId", "Sözleşme bulunamadı.", p => p.ContractId, (p, v) => p.ContractId = v!.Value, required: true, createOnly: true),
        Field.Choice<ContractParty>("role", PartyRoles.All, p => p.Role, (p, v) => p.Role = v),
        Field.Shared<ContractParty, Party>("partyId", "Taraf bulunamadı.", p => p.PartyId, (p, v) => p.PartyId = v),
        Field.Shared<ContractParty, Person>("personId", "Kişi bulunamadı.", p => p.PersonId, (p, v) => p.PersonId = v),
        Field.Integer<ContractParty>("position", p => p.Position, (p, v) => { if (v is { } n) { p.Position = n; } }, max: 9999),
        Field.Text<ContractParty>("notes", ContractParty.NotesMaxLength, p => p.Notes, (p, v) => p.Notes = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractParty>> Filters { get; } = new Dictionary<string, RecordFilter<ContractParty>>
    {
        ["contractId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.ContractId == id) : null,
        ["partyId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.PartyId == id) : null,
        ["personId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(p => p.PersonId == id) : null,
        ["role"] = (q, v, _) => PartyRoles.All.Contains(v) ? q.Where(p => p.Role == v) : null,
    };

    protected override IOrderedQueryable<ContractParty> Order(IQueryable<ContractParty> source) => source.OrderBy(p => p.Position).ThenBy(p => p.CreatedAt);

    protected override IQueryable<ContractParty> Search(IQueryable<ContractParty> source, string term) =>
        source.Where(p => p.Notes != null && p.Notes.Contains(term));

    protected override IQueryable<ContractParty> ScopeToOwners(IQueryable<ContractParty> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var contracts = Context.Data.Set<Contract>();
        return source.Where(p => contracts.Any(c => c.Id == p.ContractId && ids.Contains(c.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        ContractChildren.ContractIdOf(record) is { } contractId
            ? ContractChildren.IsContractInScopeAsync(Context, contractId, owners, cancellationToken)
            : Task.FromResult(false);

    protected override async Task AppliedAsync(ContractParty entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var contract = await ContractChildren.EditableContractAsync(Context, entity.ContractId, "contractId", cancellationToken);
        var errors = new List<RecordFieldError>();
        if (entity.PartyId is null && entity.PersonId is null)
        {
            errors.Add(new RecordFieldError("partyId", "Taraf ya da kişi seçin."));
        }

        if (entity.PartyId is { } partyId && entity.PersonId is { } personId
            && !await Context.Data.Set<Person>().AsNoTracking().AnyAsync(p => p.Id == personId && p.PartyId == partyId, cancellationToken))
        {
            errors.Add(new RecordFieldError("personId", "Kişi seçilen tarafla ilişkili değil."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (isNew && values["position"] is null)
        {
            entity.Position = await Context.Data.Set<ContractParty>().CountAsync(p => p.ContractId == contract.Id, cancellationToken) + 1;
        }

        contract.Touch();
    }

    protected override async Task BeforeDeleteAsync(ContractParty entity, CancellationToken cancellationToken)
    {
        var contract = await ContractChildren.EditableContractAsync(Context, entity.ContractId, "contractId", cancellationToken);
        contract.Touch();
    }
}

/// <summary>
/// Sözleşme kalemleri. Tutarlar sunucuda hesaplanır (kalem ve başlık toplamı aynı işlemde yenilenir). Kalem fiyatı ve tutarları hassas
/// alandır (<c>contracts.financials.read</c>); izni olmayan kalemi görür, fiyatı görmez ve yazamaz.
/// </summary>
internal sealed class ContractLineRecordStore(ContractsStoreContext context) : ContractsRecordStore<ContractLine>(context)
{
    public override string EntityKey => ContractsModule.Entities.ContractLine;

    protected override string ReadPermission => ContractsModule.Permissions.ContractsRead;

    protected override string WritePermission => ContractsModule.Permissions.ContractsWrite;

    protected override bool UsesScope => true;

    protected override IReadOnlyList<RecordField<ContractLine>> Fields { get; } =
    [
        Field.Reference<ContractLine, Contract>("contractId", "Sözleşme bulunamadı.", l => l.ContractId, (l, v) => l.ContractId = v!.Value, required: true, createOnly: true),
        Field.Id<ContractLine>("stableKey", l => l.StableKey == Guid.Empty ? null : l.StableKey, (_, _) => { }).AsSystem(),
        Field.Integer<ContractLine>("position", l => l.Position, (l, v) => { if (v is { } n) { l.Position = n; } }, max: 9999),
        Field.Choice<ContractLine>("kind", LineKinds.All, l => l.Kind, (l, v) => l.Kind = v),
        Field.Text<ContractLine>("productCode", ContractLine.CodeMaxLength, l => l.ProductCode, (l, v) => l.ProductCode = v),
        Field.Text<ContractLine>("name", ContractLine.NameMaxLength, l => l.Name, (l, v) => l.Name = v!, required: true),
        Field.Text<ContractLine>("description", ContractLine.TextMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Quantity<ContractLine>("quantity", l => l.Quantity, (l, v) => l.Quantity = v ?? 1m),
        Field.Text<ContractLine>("unitName", ContractLine.UnitMaxLength, l => l.UnitName, (l, v) => l.UnitName = v),
        Field.Quantity<ContractLine>("unitPrice", l => l.UnitPrice, (l, v) => l.UnitPrice = v ?? 0m),
        Field.Rate<ContractLine>("discountPercent", l => l.DiscountPercent, (l, v) => l.DiscountPercent = v ?? 0m),
        Field.Rate<ContractLine>("taxRatePercent", l => l.TaxRatePercent, (l, v) => l.TaxRatePercent = v ?? 0m),
        Field.OptionalChoice<ContractLine>("billingPeriod", BillingPeriods.All, l => l.BillingPeriod, (l, v) => l.BillingPeriod = v),
        Field.Id<ContractLine>("sourceQuoteLineKey", l => l.SourceQuoteLineKey, (l, v) => l.SourceQuoteLineKey = v).AsSystem(),
        Field.Money<ContractLine>("listAmount", l => l.ListAmount, (l, v) => l.ListAmount = v ?? 0m).AsSystem(),
        Field.Money<ContractLine>("lineDiscount", l => l.LineDiscount, (l, v) => l.LineDiscount = v ?? 0m).AsSystem(),
        Field.Money<ContractLine>("net", l => l.Net, (l, v) => l.Net = v ?? 0m).AsSystem(),
        Field.Money<ContractLine>("tax", l => l.Tax, (l, v) => l.Tax = v ?? 0m).AsSystem(),
        Field.Money<ContractLine>("gross", l => l.Gross, (l, v) => l.Gross = v ?? 0m).AsSystem(),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<ContractLine>> Filters { get; } = new Dictionary<string, RecordFilter<ContractLine>>
    {
        ["contractId"] = (q, v, _) => FilterValue.Id(v) is { } id ? q.Where(l => l.ContractId == id) : null,
        ["kind"] = (q, v, _) => LineKinds.All.Contains(v) ? q.Where(l => l.Kind == v) : null,
    };

    protected override IOrderedQueryable<ContractLine> Order(IQueryable<ContractLine> source) => source.OrderBy(l => l.Position).ThenBy(l => l.CreatedAt);

    protected override IQueryable<ContractLine> Search(IQueryable<ContractLine> source, string term) =>
        source.Where(l => l.Name.Contains(term) || (l.ProductCode != null && l.ProductCode.Contains(term)));

    protected override IQueryable<ContractLine> ScopeToOwners(IQueryable<ContractLine> source, IReadOnlySet<Guid> owners)
    {
        var ids = owners.Select(id => (Guid?)id).ToList();
        var contracts = Context.Data.Set<Contract>();
        return source.Where(l => contracts.Any(c => c.Id == l.ContractId && ids.Contains(c.OwnerId)));
    }

    protected override Task<bool> IsInScopeAsync(RecordData record, IReadOnlySet<Guid> owners, CancellationToken cancellationToken) =>
        ContractChildren.ContractIdOf(record) is { } contractId
            ? ContractChildren.IsContractInScopeAsync(Context, contractId, owners, cancellationToken)
            : Task.FromResult(false);

    protected override Task<JsonObject> PrepareNewAsync(ContractLine entity, JsonObject values, CancellationToken cancellationToken)
    {
        entity.StableKey = Guid.CreateVersion7();
        return Task.FromResult(values);
    }

    protected override async Task AppliedAsync(ContractLine entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var contract = await ContractChildren.EditableContractAsync(Context, entity.ContractId, "contractId", cancellationToken);
        if (entity.Kind == LineKinds.Text)
        {
            // Açıklama satırı tutar taşımaz.
            entity.Quantity = 0m;
            entity.UnitPrice = 0m;
            entity.DiscountPercent = 0m;
            entity.TaxRatePercent = 0m;
            entity.BillingPeriod = null;
        }

        if (isNew && values["position"] is null)
        {
            entity.Position = await Context.Data.Set<ContractLine>().CountAsync(l => l.ContractId == contract.Id, cancellationToken) + 1;
        }

        await ContractTotals.RecalculateAsync(Context.Data, contract, isNew ? entity : null, null, cancellationToken);
        contract.Touch();
    }

    protected override async Task BeforeDeleteAsync(ContractLine entity, CancellationToken cancellationToken)
    {
        var contract = await ContractChildren.EditableContractAsync(Context, entity.ContractId, "contractId", cancellationToken);
        await ContractTotals.RecalculateAsync(Context.Data, contract, null, entity.Id, cancellationToken);
        contract.Touch();
    }
}

/// <summary>Sözleşme tarihçesinin salt okunur erişimi. Çağıran, sözleşmenin görünürlüğünü kayıt boru hattında kendisi doğrulamış olmalıdır.</summary>
internal sealed class ContractReader(Core.Contracts.Persistence.IModuleData data)
{
    public async Task<List<ContractEvent>> HistoryAsync(Guid contractId, CancellationToken cancellationToken) =>
        await data.Set<ContractEvent>().AsNoTracking().Where(e => e.ContractId == contractId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);
}
