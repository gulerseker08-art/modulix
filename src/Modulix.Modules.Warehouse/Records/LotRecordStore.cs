using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Parti kayıtları (ADR-0032 K3). Ürün, malik ve beyanname satırı oluştururken seçilir ve sonra değişmez (aynı ürün başka malik ya da satırla başka partidir);
/// faturalanan taraf, açıklama ve etkin bayrağı değişebilir. Parti numarası tenant içinde benzersizdir (büyük harfe çevrilir). Hareketi olan parti silinemez.
/// </summary>
internal sealed partial class LotRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<Lot>(context)
{
    public override string EntityKey => WarehouseModule.Entities.Lot;

    protected override string ReadPermission => WarehouseModule.Permissions.LotsRead;

    protected override string WritePermission => WarehouseModule.Permissions.LotsWrite;

    protected override IReadOnlyList<RecordField<Lot>> Fields { get; } =
    [
        Field.Text<Lot>("lotNo", Lot.LotNoMaxLength, l => l.LotNo, (l, v) => l.LotNo = v!, required: true, upperCase: true),
        Field.Reference<Lot, StockItem>("stockItemId", "Stok kartı bulunamadı.", l => l.StockItemId, (l, v) => l.StockItemId = v!.Value, required: true, createOnly: true),
        Field.PartyRef<Lot>("ownerPartyId", "Malik bulunamadı.", l => l.OwnerPartyId, (l, v) => l.OwnerPartyId = v!.Value, required: true, createOnly: true),
        Field.PartyRef<Lot>("billToPartyId", "Faturalanan taraf bulunamadı.", l => l.BillToPartyId, (l, v) => l.BillToPartyId = v),
        Field.Reference<Lot, CustomsDeclarationLine>("declarationLineId", "Beyanname satırı bulunamadı.", l => l.DeclarationLineId, (l, v) => l.DeclarationLineId = v, createOnly: true),
        Field.Text<Lot>("description", Lot.DescriptionMaxLength, l => l.Description, (l, v) => l.Description = v),
        Field.Flag<Lot>("isActive", l => l.IsActive, (l, v) => l.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Lot>> Filters { get; } = new Dictionary<string, RecordFilter<Lot>>
    {
        ["stockItemId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.StockItemId == id) : null,
        // Firma silme/birleştirme koruması bu iki süzgeçle kullanım sayar (SharedRecordReferences).
        ["ownerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.OwnerPartyId == id) : null,
        ["billToPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.BillToPartyId == id) : null,
        ["declarationLineId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.DeclarationLineId == id) : null,
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(l => l.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<Lot> Order(IQueryable<Lot> source) => source.OrderBy(l => l.LotNo);

    // Liste başlıklarına göre sıralama (sort=lotNo, sort=-lotNo ...); eşit kayıtlar parti numarasına göre sıralanır. Firma adına göre sıralama yok
    // (firma görünürlüğü sıralamayla dolanılmasın).
    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("lotNo", "isActive", "createdAt");

    protected override IOrderedQueryable<Lot> Order(IQueryable<Lot> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "lotNo" => By(source, l => l.LotNo, desc),
            "isActive" => By(source, l => l.IsActive, desc).ThenBy(l => l.LotNo),
            "createdAt" => By(source, l => l.CreatedAt, desc).ThenBy(l => l.LotNo),
            _ => Order(source),
        };
    }

    // Firma adıyla arama yok: firma görünürlüğü (CRM kapsamı) bu ekrandan dolanılmasın.
    protected override IQueryable<Lot> Search(IQueryable<Lot> source, string term) =>
        source.Where(l => l.LotNo.Contains(term) || (l.Description != null && l.Description.Contains(term)));

    protected override async Task AppliedAsync(Lot entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        if (!LotNoPattern().IsMatch(entity.LotNo))
        {
            errors.Add(new RecordFieldError("lotNo", "Parti numarası yalnızca harf, rakam ve - _ . / karakterlerini içerebilir."));
        }
        else if (await Context.Data.Set<Lot>().AsNoTracking().AnyAsync(l => l.LotNo == entity.LotNo && l.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("lotNo", "Bu parti numarası başka bir partide kullanılıyor."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(Lot entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<StockMovement>().AsNoTracking().AnyAsync(m => m.LotId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Partinin stok hareketleri var; hareket defteri silinmez. Partiyi pasife alın.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]*$")]
    private static partial Regex LotNoPattern();
}
