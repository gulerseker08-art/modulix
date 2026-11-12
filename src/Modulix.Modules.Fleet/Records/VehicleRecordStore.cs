using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Fleet.Domain;

namespace Modulix.Modules.Fleet.Records;

/// <summary>
/// Araç kayıtları (ADR-0038 K1, dilim 1a). Plaka boşluksuz büyük harfe çevrilir ve tenant içinde benzersizdir; şasi numarası verilirse
/// 17 karakterdir ve benzersizdir. Taşeron araçta taşıyıcı firma zorunludur ve firmanın "Taşıyıcı" ilişki rolü olmalıdır; diğer mülkiyet
/// türlerinde taşıyıcı seçilmez. Araç silinmez (K12): kullanım dışı araç "Pasif" yapılır.
/// </summary>
internal sealed partial class VehicleRecordStore(FleetStoreContext context) : FleetRecordStore<Vehicle>(context), IRecordLabelProvider
{
    /// <summary>Aracı başka listelerde (süreli belge) plakasıyla tanıtır.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> LabelsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        Guid[] wanted = [.. ids];
        return await Context.Data.Set<Vehicle>().AsNoTracking().Where(v => wanted.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Plate, cancellationToken);
    }

    public override string EntityKey => FleetModule.Entities.Vehicle;

    protected override string ReadPermission => FleetModule.Permissions.VehiclesRead;

    protected override string WritePermission => FleetModule.Permissions.VehiclesWrite;

    protected override IReadOnlyList<RecordField<Vehicle>> Fields { get; } =
    [
        Field.Text<Vehicle>("plate", Vehicle.PlateMaxLength, v => v.Plate, (v, x) => v.Plate = x!, required: true, upperCase: true),
        Field.ListChoice<Vehicle>("kindKey", FleetModule.Lists.VehicleKind, v => v.KindKey, (v, x) => v.KindKey = x ?? string.Empty),
        Field.Choice<Vehicle>("ownership", Vehicle.Ownerships.All, v => v.Ownership, (v, x) => v.Ownership = x),
        Field.PartyRef<Vehicle>("carrierPartyId", "Taşıyıcı firma bulunamadı.", v => v.CarrierPartyId, (v, x) => v.CarrierPartyId = x),
        Field.Text<Vehicle>("brand", Vehicle.BrandMaxLength, v => v.Brand, (v, x) => v.Brand = x),
        Field.Text<Vehicle>("model", Vehicle.ModelMaxLength, v => v.Model, (v, x) => v.Model = x),
        Field.Integer<Vehicle>("modelYear", v => v.ModelYear, (v, x) => v.ModelYear = x, 1950, 2100),
        Field.Text<Vehicle>("vin", Vehicle.VinLength, v => v.Vin, (v, x) => v.Vin = x, upperCase: true),
        Field.Decimal<Vehicle>("payloadKg", v => v.PayloadKg, (v, x) => v.PayloadKg = x, 2, 0m, 100_000m),
        Field.Decimal<Vehicle>("volumeM3", v => v.VolumeM3, (v, x) => v.VolumeM3 = x, 2, 0m, 1_000m),
        Field.Decimal<Vehicle>("loadingMeters", v => v.LoadingMeters, (v, x) => v.LoadingMeters = x, 2, 0m, 30m),
        Field.Integer<Vehicle>("palletCapacity", v => v.PalletCapacity, (v, x) => v.PalletCapacity = x, 0, 100),
        Field.Choice<Vehicle>("status", Vehicle.Statuses.All, v => v.Status, (v, x) => v.Status = x),
        Field.Text<Vehicle>("notes", Vehicle.NotesMaxLength, v => v.Notes, (v, x) => v.Notes = x),

        // Bakım kayıtlarından türetilir (dilim 1b); genel uçtan gelen değer yok sayılır.
        Field.Computed<Vehicle>("lastMaintenanceOn", v => Day(v.LastMaintenanceOn)),
        Field.Computed<Vehicle>("odometerKm", v => v.OdometerKm is { } km ? JsonValue.Create(km) : null),
        Field.Computed<Vehicle>("nextMaintenanceDate", v => Day(v.NextMaintenanceDate)),
        Field.Computed<Vehicle>("nextMaintenanceKm", v => v.NextMaintenanceKm is { } km ? JsonValue.Create(km) : null),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Vehicle>> Filters { get; } = new Dictionary<string, RecordFilter<Vehicle>>
    {
        ["status"] = (q, v) => Vehicle.Statuses.All.Contains(v) ? q.Where(x => x.Status == v) : null,
        ["ownership"] = (q, v) => Vehicle.Ownerships.All.Contains(v) ? q.Where(x => x.Ownership == v) : null,
        ["kindKey"] = (q, v) => v.Length is > 0 and <= 100 ? q.Where(x => x.KindKey == v) : null,
        ["carrierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(x => x.CarrierPartyId == id) : null,

        // Bakımı yaklaşan ya da geciken araçlar: sonraki bakım tarihi bu güne kadar olanlar (dilim 1b).
        ["maintenanceDueBy"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(x => x.NextMaintenanceDate != null && x.NextMaintenanceDate <= day) : null,
    };

    protected override IOrderedQueryable<Vehicle> Order(IQueryable<Vehicle> source) => source.OrderBy(v => v.Plate);

    // Liste başlıklarına göre sıralama (sort=plate, sort=-plate ...); eşit kayıtlar plakaya göre sıralanır.
    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("plate", "kindKey", "ownership", "brand", "modelYear", "payloadKg", "status", "nextMaintenanceDate", "createdAt");

    protected override IOrderedQueryable<Vehicle> Order(IQueryable<Vehicle> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "plate" => By(source, v => v.Plate, desc),
            "kindKey" => By(source, v => v.KindKey, desc).ThenBy(v => v.Plate),
            "ownership" => By(source, v => v.Ownership, desc).ThenBy(v => v.Plate),
            "brand" => ByNullsLast(source, v => v.Brand, desc).ThenBy(v => v.Plate),
            "modelYear" => ByNullsLast(source, v => v.ModelYear, desc).ThenBy(v => v.Plate),
            "payloadKg" => ByNullsLast(source, v => v.PayloadKg, desc).ThenBy(v => v.Plate),
            "status" => By(source, v => v.Status, desc).ThenBy(v => v.Plate),
            "nextMaintenanceDate" => ByNullsLast(source, v => v.NextMaintenanceDate, desc).ThenBy(v => v.Plate),
            "createdAt" => By(source, v => v.CreatedAt, desc).ThenBy(v => v.Plate),
            _ => Order(source),
        };
    }

    protected override IQueryable<Vehicle> Search(IQueryable<Vehicle> source, string term)
    {
        var plate = Normalize(term);
        return source.Where(v => v.Plate.Contains(plate) || (v.Brand != null && v.Brand.Contains(term))
            || (v.Model != null && v.Model.Contains(term)) || (v.Vin != null && v.Vin.Contains(plate)));
    }

    protected override async Task AppliedAsync(Vehicle entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var errors = new List<RecordFieldError>();

        entity.Plate = Normalize(entity.Plate);
        if (!PlatePattern().IsMatch(entity.Plate))
        {
            errors.Add(new RecordFieldError("plate", "Plaka yalnızca harf ve rakam içerebilir (boşluklar kaldırılır)."));
        }
        else if (await Context.Data.Set<Vehicle>().AsNoTracking().AnyAsync(v => v.Plate == entity.Plate && v.Id != entity.Id, cancellationToken))
        {
            errors.Add(new RecordFieldError("plate", "Bu plaka başka bir araçta kayıtlı."));
        }

        if (entity.Vin is { } vin)
        {
            entity.Vin = Normalize(vin);
            if (!VinPattern().IsMatch(entity.Vin))
            {
                errors.Add(new RecordFieldError("vin", "Şasi numarası 17 karakter olmalı; I, O ve Q harflerini içermez."));
            }
            else if (await Context.Data.Set<Vehicle>().AsNoTracking().AnyAsync(v => v.Vin == entity.Vin && v.Id != entity.Id, cancellationToken))
            {
                errors.Add(new RecordFieldError("vin", "Bu şasi numarası başka bir araçta kayıtlı."));
            }
        }

        if (entity.Ownership == Vehicle.Ownerships.Subcontracted)
        {
            if (entity.CarrierPartyId is not { } carrier)
            {
                errors.Add(new RecordFieldError("carrierPartyId", "Taşeron araçta taşıyıcı firma seçilmelidir."));
            }
            else if (values.ContainsKey("carrierPartyId") || values.ContainsKey("ownership") || isNew)
            {
                // Rol, taşıyıcı ya da mülkiyet değişince denetlenir; firmadan rol sonradan kalksa da kayıtlı araç bozulmaz.
                var roles = await Context.Data.Set<Party>().AsNoTracking()
                    .Where(p => p.Id == carrier).Select(p => p.RelationshipRoles).SingleOrDefaultAsync(cancellationToken);
                if (!(roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains(FleetModule.RelationshipRoles.Carrier))
                {
                    errors.Add(new RecordFieldError(
                        "carrierPartyId", "Seçilen firma \"Taşıyıcı\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin."));
                }
            }
        }
        else if (entity.CarrierPartyId is not null)
        {
            errors.Add(new RecordFieldError("carrierPartyId", "Taşıyıcı firma yalnızca taşeron araçta seçilir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }
    }

    protected override Task BeforeDeleteAsync(Vehicle entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Araçlar silinmez; kullanım dışı aracın durumunu \"Pasif\" yapın.");

    private static JsonNode? Day(DateOnly? day) => day is { } d ? JsonValue.Create(d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) : null;

    /// <summary>Plaka ve şasi numarası boşluksuz, büyük harfle saklanır ve aranır ("34 abc 123" = "34ABC123").</summary>
    internal static string Normalize(string value) =>
        string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();

    [GeneratedRegex("^[A-Z0-9ÇĞİÖŞÜ]{2,20}$")]
    private static partial Regex PlatePattern();

    [GeneratedRegex("^[A-HJ-NPR-Z0-9]{17}$")]
    private static partial Regex VinPattern();
}
