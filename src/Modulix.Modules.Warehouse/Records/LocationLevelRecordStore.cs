using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Lokasyon seviye tanımları (K11). Her tesis kendi seviyelerini tanımlar: sıra, teknik anahtar, görünen ad, izinli karakterler,
/// uzunluk aralığı ve ayraç. Sıra ve anahtar tesis içinde benzersizdir; tesis sonradan değiştirilemez. Kullanımdaki tesiste yalnızca
/// ekleme ve yeniden adlandırma yapılabilmesi kuralı, lokasyonlar bu tanıma bağlandığında bu depoya eklenir.
/// </summary>
internal sealed partial class LocationLevelRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<LocationLevel>(context)
{
    public override string EntityKey => WarehouseModule.Entities.LocationLevel;

    protected override string ReadPermission => WarehouseModule.Permissions.LocationLevelsRead;

    protected override string WritePermission => WarehouseModule.Permissions.LocationLevelsWrite;

    protected override IReadOnlyList<RecordField<LocationLevel>> Fields { get; } =
    [
        Field.Reference<LocationLevel, Facility>("facilityId", "Tesis bulunamadı.", l => l.FacilityId, (l, v) => l.FacilityId = v!.Value, required: true, createOnly: true),
        Field.Integer<LocationLevel>("position", l => l.Position, (l, v) => l.Position = v ?? l.Position, 1, LocationLevel.MaxPosition, required: true),
        Field.Text<LocationLevel>("key", LocationLevel.KeyMaxLength, l => l.Key, (l, v) => l.Key = v!, required: true),
        Field.Text<LocationLevel>("name", LocationLevel.NameMaxLength, l => l.Name, (l, v) => l.Name = v!, required: true),
        Field.Choice<LocationLevel>("characterSet", LocationLevel.CharacterSets.All, l => l.CharacterSet, (l, v) => l.CharacterSet = v),
        Field.Integer<LocationLevel>("minLength", l => l.MinLength, (l, v) => l.MinLength = v ?? l.MinLength, 1, LocationLevel.MaxValueLength),
        Field.Integer<LocationLevel>("maxLength", l => l.MaxLength, (l, v) => l.MaxLength = v ?? l.MaxLength, 1, LocationLevel.MaxValueLength),
        Field.Text<LocationLevel>("separator", LocationLevel.SeparatorMaxLength, l => l.Separator, (l, v) => l.Separator = v ?? string.Empty),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<LocationLevel>> Filters { get; } = new Dictionary<string, RecordFilter<LocationLevel>>
    {
        ["facilityId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.FacilityId == id) : null,
    };

    protected override IOrderedQueryable<LocationLevel> Order(IQueryable<LocationLevel> source) =>
        source.OrderBy(l => l.FacilityId).ThenBy(l => l.Position);

    protected override IQueryable<LocationLevel> Search(IQueryable<LocationLevel> source, string term) =>
        source.Where(l => l.Key.Contains(term) || l.Name.Contains(term));

    protected override async Task AppliedAsync(LocationLevel entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        entity.Key = entity.Key.ToLowerInvariant();
        if (!KeyPattern().IsMatch(entity.Key))
        {
            errors.Add(new RecordFieldError("key", "Anahtar küçük harfle başlamalı; yalnızca harf, rakam ve alt çizgi içerebilir."));
        }

        if (entity.MinLength > entity.MaxLength)
        {
            errors.Add(new RecordFieldError("minLength", "En az uzunluk, en çok uzunluktan büyük olamaz."));
        }

        if (!entity.Separator.All(LocationLevel.SeparatorCharacters.Contains))
        {
            errors.Add(new RecordFieldError("separator", $"Ayraç yalnızca şu karakterlerden oluşabilir: {LocationLevel.SeparatorCharacters}"));
        }

        var siblings = Context.Data.Set<LocationLevel>().AsNoTracking().Where(l => l.FacilityId == entity.FacilityId && l.Id != entity.Id);
        if (await siblings.AnyAsync(l => l.Position == entity.Position, cancellationToken))
        {
            errors.Add(new RecordFieldError("position", "Bu sırada başka bir seviye var."));
        }

        if (await siblings.AnyAsync(l => l.Key == entity.Key, cancellationToken))
        {
            errors.Add(new RecordFieldError("key", "Bu anahtar tesisteki başka bir seviyede kullanılıyor."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
