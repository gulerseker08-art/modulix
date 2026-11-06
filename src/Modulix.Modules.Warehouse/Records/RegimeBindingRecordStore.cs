using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Rejim–profil bağlaması (ADR-0032 K16): tenant, kendi rejim listesindeki bir değeri platformun yayınladığı bir profile bağlar. Yalnızca
/// yayındaki etkin profiller seçilebilir. Rejim başına tek bağlama vardır. Tenant profildeki düzeyi yalnızca sıkılaştırabilir; profil
/// <c>Block</c> ise "Warn" seçimi etkisizdir (etkin düzey iki düzeyden sıkı olandır), bu yüzden gevşetme seçeneği zararsızdır ama sunulmaz.
/// </summary>
internal sealed class RegimeBindingRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<RegimeBinding>(context)
{
    private static readonly string[] Levels = [RegimeBinding.Inherit, RegimeBinding.Warn, RegimeBinding.Block];

    public override string EntityKey => WarehouseModule.Entities.RegimeBinding;

    protected override string ReadPermission => WarehouseModule.Permissions.RegimesRead;

    protected override string WritePermission => WarehouseModule.Permissions.RegimesWrite;

    protected override IReadOnlyList<RecordField<RegimeBinding>> Fields { get; } =
    [
        Field.ListChoice<RegimeBinding>("regimeKey", WarehouseModule.Lists.CustomsRegime, b => b.RegimeKey, (b, v) => b.RegimeKey = v ?? string.Empty),
        Field.ProfileRef<RegimeBinding>("profileId", "Rejim profili bulunamadı ya da yayında değil.", b => b.ProfileId, (b, v) => b.ProfileId = v!.Value, required: true),
        Field.Choice<RegimeBinding>("enforcement", Levels, b => b.Enforcement, (b, v) => b.Enforcement = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<RegimeBinding>> Filters { get; } = new Dictionary<string, RecordFilter<RegimeBinding>>
    {
        ["regimeKey"] = (q, v) => ChoiceValueKeys.IsValid(v) ? q.Where(b => b.RegimeKey == v) : null,
    };

    protected override IOrderedQueryable<RegimeBinding> Order(IQueryable<RegimeBinding> source) => source.OrderBy(b => b.RegimeKey);

    protected override IQueryable<RegimeBinding> Search(IQueryable<RegimeBinding> source, string term) => source.Where(b => b.RegimeKey.Contains(term));

    protected override async Task AppliedAsync(RegimeBinding entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<RegimeBinding>().AsNoTracking().AnyAsync(b => b.RegimeKey == entity.RegimeKey && b.Id != entity.Id, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("regimeKey", "Bu rejim zaten bir profile bağlı; mevcut bağlamayı düzenleyin.")]);
        }
    }
}
