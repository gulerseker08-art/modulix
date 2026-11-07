using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Lokasyon seviye tanımları (K11). Her tesis kendi seviyelerini tanımlar: sıra, teknik anahtar, görünen ad, izinli karakterler,
/// uzunluk aralığı ve ayraç. Sıra ve anahtar tesis içinde benzersizdir; tesis sonradan değiştirilemez. Lokasyonu olan (kullanımdaki)
/// tesiste tanım yalnızca eklenir ve yeniden adlandırılır: yeni seviye mevcutların sonuna gelir, diğer alanlar değişmez, kullanılan
/// seviye silinemez; böylece üretilmiş lokasyon kodları bozulmaz.
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

    protected override IOrderedQueryable<LocationLevel> Order(IQueryable<LocationLevel> source) => Order(source, "facility");

    // Liste başlıklarına göre sıralama; tesis, tesisin koduna göre sıralanır. Eşit kayıtlar tesise ve sıraya göre sıralanır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("facility", "position", "name", "key", "characterSet", "maxLength", "separator");

    protected override IOrderedQueryable<LocationLevel> Order(IQueryable<LocationLevel> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        var facilities = Context.Data.Set<Facility>();
        return sort?.TrimStart('-') switch
        {
            "facility" => By(source, l => facilities.Where(f => f.Id == l.FacilityId).Select(f => f.Code).FirstOrDefault(), desc).ThenBy(l => l.Position),
            "position" => By(source, l => l.Position, desc).ThenBy(l => l.FacilityId),
            "name" => By(source, l => l.Name, desc).ThenBy(l => l.FacilityId).ThenBy(l => l.Position),
            "key" => By(source, l => l.Key, desc).ThenBy(l => l.FacilityId).ThenBy(l => l.Position),
            "characterSet" => By(source, l => l.CharacterSet, desc).ThenBy(l => l.FacilityId).ThenBy(l => l.Position),
            "maxLength" => By(source, l => l.MaxLength, desc).ThenBy(l => l.FacilityId).ThenBy(l => l.Position),
            "separator" => By(source, l => l.Separator, desc).ThenBy(l => l.FacilityId).ThenBy(l => l.Position),
            _ => Order(source),
        };
    }

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

        // Kullanımdaki tesiste (lokasyonu olan) tanım yalnızca eklenir ve yeniden adlandırılır (K11): mevcut kodlar bozulmaz.
        if (await Context.Data.Set<Location>().AsNoTracking().AnyAsync(l => l.FacilityId == entity.FacilityId, cancellationToken))
        {
            if (isNew)
            {
                var deepest = await siblings.MaxAsync(l => (int?)l.Position, cancellationToken) ?? 0;
                if (entity.Position <= deepest)
                {
                    errors.Add(new RecordFieldError("position", "Lokasyonu olan tesiste yeni seviye yalnızca mevcut seviyelerden sonra eklenebilir."));
                }
            }
            else
            {
                var before = await Context.Data.Set<LocationLevel>().AsNoTracking().SingleAsync(l => l.Id == entity.Id, cancellationToken);
                var changed = new Dictionary<string, bool>
                {
                    ["position"] = before.Position != entity.Position,
                    ["key"] = before.Key != entity.Key,
                    ["characterSet"] = before.CharacterSet != entity.CharacterSet,
                    ["minLength"] = before.MinLength != entity.MinLength,
                    ["maxLength"] = before.MaxLength != entity.MaxLength,
                    ["separator"] = before.Separator != entity.Separator,
                };
                errors.AddRange(changed.Where(c => c.Value)
                    .Select(c => new RecordFieldError(c.Key, "Lokasyonu olan tesiste seviye tanımı değiştirilemez; yalnızca ad değişir.")));
            }
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override async Task BeforeDeleteAsync(LocationLevel entity, CancellationToken cancellationToken)
    {
        // Seviyenin sırası: tesisin tanımları arasında kaçıncı. O derinlikte ya da daha derinde lokasyon varsa seviye silinemez.
        var rank = await Context.Data.Set<LocationLevel>().AsNoTracking()
            .CountAsync(l => l.FacilityId == entity.FacilityId && l.Position < entity.Position, cancellationToken) + 1;
        if (await Context.Data.Set<Location>().AsNoTracking().AnyAsync(l => l.FacilityId == entity.FacilityId && l.Depth >= rank, cancellationToken))
        {
            throw new RecordConflictException("Bu seviyeyi kullanan lokasyonlar var; önce lokasyonları silin.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex KeyPattern();
}
