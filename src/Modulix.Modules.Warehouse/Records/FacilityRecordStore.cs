using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Tesis kayıtları (ADR-0032). Kod tenant içinde benzersizdir ve büyük harfe çevrilir (lokasyon kodlarının ilk bölümü olabilir);
/// ülke kodu isteğe bağlıdır. Gümrük uzantısı bayrağı yalnızca tesisin gümrük alanlarını açıp açmadığını söyler; yasal kurallar
/// rejim profilindedir (K16). Alanlar, lokasyon seviyeleri ve lokasyonlar tesise bağlandığında silme engeli de buraya eklenir.
/// </summary>
internal sealed partial class FacilityRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<Facility>(context)
{
    public override string EntityKey => WarehouseModule.Entities.Facility;

    protected override string ReadPermission => WarehouseModule.Permissions.FacilitiesRead;

    protected override string WritePermission => WarehouseModule.Permissions.FacilitiesWrite;

    protected override IReadOnlyList<RecordField<Facility>> Fields { get; } =
    [
        Field.Text<Facility>("code", Facility.CodeMaxLength, f => f.Code, (f, v) => f.Code = v!, required: true),
        Field.Text<Facility>("name", Facility.NameMaxLength, f => f.Name, (f, v) => f.Name = v!, required: true),
        Field.Text<Facility>("description", Facility.DescriptionMaxLength, f => f.Description, (f, v) => f.Description = v),
        Field.Text<Facility>("countryCode", Facility.CountryCodeLength, f => f.CountryCode, (f, v) => f.CountryCode = v),
        Field.Flag<Facility>("customsEnabled", f => f.CustomsEnabled, (f, v) => f.CustomsEnabled = v),
        Field.Flag<Facility>("isActive", f => f.IsActive, (f, v) => f.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Facility>> Filters { get; } = new Dictionary<string, RecordFilter<Facility>>
    {
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(f => f.IsActive == flag) : null,
        ["customsEnabled"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(f => f.CustomsEnabled == flag) : null,
    };

    protected override IOrderedQueryable<Facility> Order(IQueryable<Facility> source) => source.OrderBy(f => f.Code);

    protected override IQueryable<Facility> Search(IQueryable<Facility> source, string term) =>
        source.Where(f => f.Code.Contains(term) || f.Name.Contains(term));

    protected override async Task AppliedAsync(Facility entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        entity.Code = entity.Code.ToUpperInvariant();
        if (!CodePattern().IsMatch(entity.Code))
        {
            errors.Add(new RecordFieldError("code", "Kod yalnızca harf, rakam ve alt çizgi içerebilir."));
        }
        else if (await Context.Data.Set<Facility>().AsNoTracking().AnyAsync(f => f.Code == entity.Code && f.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("code", "Bu kod başka bir tesiste kullanılıyor."));
        }

        if (entity.CountryCode is { } country)
        {
            if (country.Length != Facility.CountryCodeLength || !country.All(char.IsAsciiLetter))
            {
                errors.Add(new RecordFieldError("countryCode", "İki harfli ülke kodu girin (ör. TR)."));
            }
            else
            {
                entity.CountryCode = country.ToUpperInvariant();
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    [GeneratedRegex("^[A-Z0-9_]+$")]
    private static partial Regex CodePattern();
}
