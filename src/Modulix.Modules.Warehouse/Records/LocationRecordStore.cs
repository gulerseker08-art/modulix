using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

/// <summary>
/// Lokasyon ağacı (K11). Düğümün seviyesi derinliğinden gelir ve değeri o seviyenin tanımına (karakter kümesi, uzunluk) uymalıdır;
/// kod üst kod + ayraç + değerden sunucuda üretilir ve tesis içinde benzersizdir. Tesis, üst düğüm, değer ve kod oluşturulduktan sonra
/// değişmez. Alan üst düğümden kalıtılır (üst düğümün alanı varsa alt düğüm aynısını taşır); alt düğümü olan düğümün alanı değişmez.
/// Alt düğümü olan düğüm silinemez.
/// </summary>
internal sealed class LocationRecordStore(WarehouseStoreContext context) : WarehouseRecordStore<Location>(context)
{
    public override string EntityKey => WarehouseModule.Entities.Location;

    protected override string ReadPermission => WarehouseModule.Permissions.LocationsRead;

    protected override string WritePermission => WarehouseModule.Permissions.LocationsWrite;

    protected override IReadOnlyList<RecordField<Location>> Fields { get; } =
    [
        Field.Reference<Location, Facility>("facilityId", "Tesis bulunamadı.", l => l.FacilityId, (l, v) => l.FacilityId = v!.Value, required: true, createOnly: true),
        Field.Reference<Location, Location>("parentId", "Üst lokasyon bulunamadı.", l => l.ParentId, (l, v) => l.ParentId = v, createOnly: true),
        Field.Reference<Location, Zone>("zoneId", "Alan bulunamadı.", l => l.ZoneId, (l, v) => l.ZoneId = v),
        Field.Text<Location>("value", Location.ValueMaxLength, l => l.Value, (l, v) => l.Value = v!, required: true, createOnly: true, upperCase: true),
        Field.Integer<Location>("depth", l => l.Depth, (l, v) => l.Depth = v ?? l.Depth, 1, LocationLevel.MaxPosition).AsSystem(),
        Field.Text<Location>("code", Location.CodeMaxLength, l => l.Code, (l, v) => l.Code = v!).AsSystem(),
        Field.Text<Location>("name", Location.NameMaxLength, l => l.Name, (l, v) => l.Name = v),
        Field.Flag<Location>("isActive", l => l.IsActive, (l, v) => l.IsActive = v),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Location>> Filters { get; } = new Dictionary<string, RecordFilter<Location>>
    {
        ["facilityId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.FacilityId == id) : null,
        ["parentId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.ParentId == id) : null,
        ["zoneId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(l => l.ZoneId == id) : null,
        ["root"] = (q, v) => FilterValue.Bool(v) is { } root ? (root ? q.Where(l => l.ParentId == null) : q.Where(l => l.ParentId != null)) : null,
        ["isActive"] = (q, v) => FilterValue.Bool(v) is { } flag ? q.Where(l => l.IsActive == flag) : null,
    };

    protected override IOrderedQueryable<Location> Order(IQueryable<Location> source) => Order(source, "facility");

    // Liste başlıklarına göre sıralama; tesis ve alan kodlarına göre sıralanır (alanı olmayanlar sonda). Eşit kayıtlar koda göre sıralanır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("code", "name", "facility", "zone", "depth", "isActive");

    protected override IOrderedQueryable<Location> Order(IQueryable<Location> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        var facilities = Context.Data.Set<Facility>();
        var zones = Context.Data.Set<Zone>();
        return sort?.TrimStart('-') switch
        {
            "code" => By(source, l => l.Code, desc).ThenBy(l => l.FacilityId),
            "name" => ByNullsLast(source, l => l.Name, desc).ThenBy(l => l.Code),
            "facility" => By(source, l => facilities.Where(f => f.Id == l.FacilityId).Select(f => f.Code).FirstOrDefault(), desc).ThenBy(l => l.Code),
            "zone" => ByNullsLast(source, l => zones.Where(z => z.Id == l.ZoneId).Select(z => z.Code).FirstOrDefault(), desc).ThenBy(l => l.Code),
            "depth" => By(source, l => l.Depth, desc).ThenBy(l => l.Code),
            "isActive" => By(source, l => l.IsActive, desc).ThenBy(l => l.Code),
            _ => Order(source),
        };
    }

    protected override IQueryable<Location> Search(IQueryable<Location> source, string term) =>
        source.Where(l => l.Code.Contains(term) || (l.Name != null && l.Name.Contains(term)));

    protected override async Task AppliedAsync(Location entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();
        var data = Context.Data;

        var parent = entity.ParentId is { } parentId
            ? await data.Set<Location>().AsNoTracking().SingleAsync(l => l.Id == parentId, cancellationToken)
            : null;
        if (parent is not null && parent.FacilityId != entity.FacilityId)
        {
            errors.Add(new RecordFieldError("parentId", "Üst lokasyon başka bir tesise ait."));
        }

        if (isNew && errors.Count == 0)
        {
            await GenerateAsync(entity, parent, errors, cancellationToken);
        }

        await ApplyZoneRulesAsync(entity, parent, isNew, errors, cancellationToken);

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    /// <summary>Derinliği, değeri, kodu ve yolu seviye tanımına göre üretir ve doğrular (yalnızca oluştururken).</summary>
    private async Task GenerateAsync(Location entity, Location? parent, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        var data = Context.Data;
        var levels = await data.Set<LocationLevel>().AsNoTracking()
            .Where(l => l.FacilityId == entity.FacilityId).OrderBy(l => l.Position).ToListAsync(cancellationToken);

        entity.Depth = (parent?.Depth ?? 0) + 1;
        if (entity.Depth > levels.Count)
        {
            errors.Add(new RecordFieldError(
                entity.ParentId is null ? "facilityId" : "parentId",
                levels.Count == 0 ? "Tesiste lokasyon seviyesi tanımlı değil." : $"Tesiste {entity.Depth}. seviye tanımlı değil."));
            return;
        }

        var level = levels[entity.Depth - 1];
        if (entity.Value.Length < level.MinLength || entity.Value.Length > level.MaxLength)
        {
            errors.Add(new RecordFieldError("value", $"{level.Name} değeri {level.MinLength} ile {level.MaxLength} karakter arasında olmalıdır."));
        }
        else if (!entity.Value.All(c => AllowedIn(level.CharacterSet, c)))
        {
            errors.Add(new RecordFieldError("value", $"{level.Name} değeri yalnızca {Describe(level.CharacterSet)} içerebilir."));
        }

        if (errors.Count > 0)
        {
            return;
        }

        entity.Code = parent is null ? entity.Value : parent.Code + level.Separator + entity.Value;
        entity.Path = (parent?.Path ?? "/") + entity.Id + "/";
        if (await data.Set<Location>().AsNoTracking().AnyAsync(l => l.FacilityId == entity.FacilityId && l.Code == entity.Code, cancellationToken))
        {
            errors.Add(new RecordFieldError("value", $"{entity.Code} kodlu lokasyon zaten var."));
        }
    }

    private async Task ApplyZoneRulesAsync(Location entity, Location? parent, bool isNew, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        var data = Context.Data;

        // Üst düğümün alanı alt düğüme kalıtılır; farklı alan verilemez.
        if (parent?.ZoneId is { } inherited)
        {
            if (entity.ZoneId is null)
            {
                entity.ZoneId = inherited;
            }
            else if (entity.ZoneId != inherited)
            {
                errors.Add(new RecordFieldError("zoneId", "Alt lokasyon, üst lokasyonun alanından farklı bir alanda olamaz."));
            }
        }

        if (entity.ZoneId is { } zoneId
            && !await data.Set<Zone>().AsNoTracking().AnyAsync(z => z.Id == zoneId && z.FacilityId == entity.FacilityId, cancellationToken))
        {
            errors.Add(new RecordFieldError("zoneId", "Alan bu tesise ait değil."));
        }

        // Alt düğümleri olan düğümün alanı değişmez: alt ağacın alanı onunla birlikte tutarlı kalmalıdır.
        if (!isNew)
        {
            var current = await data.Set<Location>().AsNoTracking().Where(l => l.Id == entity.Id).Select(l => l.ZoneId).SingleAsync(cancellationToken);
            if (current != entity.ZoneId && await data.Set<Location>().AsNoTracking().AnyAsync(l => l.ParentId == entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("zoneId", "Alt lokasyonları olan lokasyonun alanı değiştirilemez."));
            }
        }
    }

    protected override async Task BeforeDeleteAsync(Location entity, CancellationToken cancellationToken)
    {
        if (await Context.Data.Set<Location>().AsNoTracking().AnyAsync(l => l.ParentId == entity.Id, cancellationToken))
        {
            throw new RecordConflictException("Alt lokasyonları olan lokasyon silinemez; önce alt lokasyonları silin.");
        }
    }

    private static bool AllowedIn(string characterSet, char c) => characterSet switch
    {
        LocationLevel.CharacterSets.Letters => char.IsAsciiLetterUpper(c),
        LocationLevel.CharacterSets.Digits => char.IsAsciiDigit(c),
        _ => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c),
    };

    private static string Describe(string characterSet) => characterSet switch
    {
        LocationLevel.CharacterSets.Letters => "harf",
        LocationLevel.CharacterSets.Digits => "rakam",
        _ => "harf ve rakam",
    };
}
