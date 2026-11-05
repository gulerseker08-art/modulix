using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Alan kayıtları (K11, K12, K18). Alan bir tesise bağlıdır (sonradan değiştirilemez); kod tesis içinde benzersizdir. Alan türünün
/// kategorisi iki kuralı yönetir: gümrük denetimli alan yalnızca gümrük uzantısı açık tesiste tanımlanabilir, ve serbest ile
/// gümrük denetimli alanlar aynı tesiste ancak tesis bunu açıkça izin veriyorsa bir arada bulunur (varsayılan kapalı, K12).
/// </summary>
internal sealed partial class ZoneRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<Zone>(context)
{
    public override string EntityKey => WarehouseModule.Entities.Zone;

    protected override string ReadPermission => WarehouseModule.Permissions.ZonesRead;

    protected override string WritePermission => WarehouseModule.Permissions.ZonesWrite;

    protected override IReadOnlyList<RecordField<Zone>> Fields { get; } =
    [
        Field.Reference<Zone, Facility>("facilityId", "Tesis bulunamadı.", z => z.FacilityId, (z, v) => z.FacilityId = v!.Value, required: true, createOnly: true),
        Field.Text<Zone>("code", Zone.CodeMaxLength, z => z.Code, (z, v) => z.Code = v!, required: true),
        Field.Text<Zone>("name", Zone.NameMaxLength, z => z.Name, (z, v) => z.Name = v!, required: true),
        Field.ListChoice<Zone>("kindKey", WarehouseModule.Lists.ZoneKind, z => z.KindKey, (z, v) => z.KindKey = v ?? string.Empty),
        Field.Text<Zone>("description", Zone.DescriptionMaxLength, z => z.Description, (z, v) => z.Description = v),
        Field.Flag<Zone>("isActive", z => z.IsActive, (z, v) => z.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Zone>> Filters { get; } = new Dictionary<string, RecordFilter<Zone>>
    {
        ["facilityId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(z => z.FacilityId == id) : null,
        ["kindKey"] = (q, v) => ChoiceValueKeysValid(v) ? q.Where(z => z.KindKey == v) : null,
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(z => z.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<Zone> Order(IQueryable<Zone> source) => source.OrderBy(z => z.FacilityId).ThenBy(z => z.Code);

    protected override IQueryable<Zone> Search(IQueryable<Zone> source, string term) =>
        source.Where(z => z.Code.Contains(term) || z.Name.Contains(term));

    protected override async Task AppliedAsync(Zone entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        entity.Code = entity.Code.ToUpperInvariant();
        if (!CodePattern().IsMatch(entity.Code))
        {
            errors.Add(new RecordFieldError("code", "Kod yalnızca harf, rakam ve alt çizgi içerebilir."));
        }
        else if (await Context.Data.Set<Zone>().AsNoTracking()
            .AnyAsync(z => z.FacilityId == entity.FacilityId && z.Code == entity.Code && z.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu kod tesisteki başka bir alanda kullanılıyor."));
        }

        var facility = await Context.Data.Set<Facility>().AsNoTracking().SingleAsync(f => f.Id == entity.FacilityId, cancellationToken);
        var category = (await ZoneRules.CategoriesAsync(Context, cancellationToken)).GetValueOrDefault(entity.KindKey);
        if (category is null)
        {
            errors.Add(new RecordFieldError("kindKey", "Alan türünün kategorisi tanımlı değil."));
        }
        else
        {
            if (category == WarehouseModule.ZoneKindCategories.Controlled && !facility.CustomsEnabled)
            {
                errors.Add(new RecordFieldError("kindKey", "Gümrük denetimli alan yalnızca gümrük uzantısı açık tesiste tanımlanabilir."));
            }

            var others = await ZoneRules.CategoriesInUseAsync(Context, entity.FacilityId, entity.Id, cancellationToken);
            others.Add(category);
            if (ZoneRules.IsMixed(others) && !facility.AllowMixedZones)
            {
                errors.Add(new RecordFieldError("kindKey", "Bu tesiste serbest ve gümrük denetimli alanlar birlikte bulunamaz (tesis ayarı kapalı)."));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(Zone entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<Location>().AsNoTracking().AnyAsync(l => l.ZoneId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Alana bağlı lokasyonlar var; önce lokasyonları başka alana taşıyın ya da silin.");
        }
    }

    private static bool ChoiceValueKeysValid(string value) => Modulix.Core.Contracts.Customization.ChoiceValueKeys.IsValid(value);

    [GeneratedRegex("^[A-Z0-9_]+$")]
    private static partial Regex CodePattern();
}
