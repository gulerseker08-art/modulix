using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Fleet;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;
using Modulix.Modules.Logistics.Records;

namespace Modulix.Modules.Logistics.Lifecycle;

/// <summary>Uygunluk bulgusu: <see cref="Blocking"/> ise sefer hazır yapılamaz, değilse uyarıdır (onayla birlikte seferde saklanır).</summary>
public sealed record TripCheckIssue(string Code, string Message, bool Blocking);

/// <summary>Seferin yükü ve aracın (dorseyle) kapasitesi; kapasitesi girilmemiş ölçü boş döner ve denetlenmez.</summary>
public sealed record TripLoad(
    int ShipmentCount, int StopCount, decimal WeightKg, decimal? PayloadKg, int Pallets, int? PalletCapacity, decimal LoadingMeters, decimal? LoadingMetersCapacity,
    decimal VolumeM3, decimal? VolumeCapacity);

/// <summary>Seferin uygunluk denetimi sonucu (ADR-0038 dilim 5).</summary>
public sealed record TripCheck(IReadOnlyList<TripCheckIssue> Issues, TripLoad Load)
{
    public bool CanConfirm => !Issues.Any(i => i.Blocking);
}

/// <summary>Firma ayarlarından gelen lojistik politikası (API host'u tenant ayar servisiyle uygular; modül Core'un ayar servisini görmez).</summary>
public interface ILogisticsPolicyProvider
{
    /// <summary><see cref="LogisticsModule.CredentialCheckModes"/> değerlerinden biri.</summary>
    Task<string> CredentialCheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sefer planlama (ADR-0038 K5, dilim 5): sevkiyatı sefere ekler ya da çıkarır ve seferin uygunluğunu denetler. Ekleme tek işlemde sevkiyatın
/// güzergâhından yükleme ve boşaltma durağı açar ve sevkiyatı "Planlandı" yapar; çıkarma bu durakları kaldırır ve sevkiyatı "Planlama bekliyor"a
/// döndürür. Her yazma kayıt boru hattından geçer (izin: sefer yazma; audit, outbox, sürüm). Yalnızca planlanan sefer değişir.
/// </summary>
public interface ITripPlanner
{
    /// <summary>Uygunluk: atama (araç/sürücü ya da taşıyıcı), durak zinciri, araç ve sürücü durumu, kapasite, süreli belgeler, çakışan seferler.</summary>
    Task<RecordResult<TripCheck>> CheckAsync(Guid tripId, CancellationToken cancellationToken);

    /// <summary>Planlama bekleyen sevkiyatı sefere ekler; sevkiyatın yeni halini döner.</summary>
    Task<RecordResult<RecordData>> AssignAsync(Guid tripId, Guid shipmentId, CancellationToken cancellationToken);

    /// <summary>Henüz yüklenmemiş sevkiyatı seferden çıkarır; sevkiyatın yeni halini döner.</summary>
    Task<RecordResult<RecordData>> UnassignAsync(Guid tripId, Guid shipmentId, CancellationToken cancellationToken);
}

internal sealed class TripPlanner(
    IRecordPipeline pipeline,
    LogisticsWorkflowGate gate,
    LogisticsStoreContext context,
    ICurrentAccess access,
    ICredentialDirectory? credentials = null,
    ILogisticsPolicyProvider? policy = null) : ITripPlanner
{
    private static readonly string[] OpenTrips = [Trip.Statuses.Planning, Trip.Statuses.Ready, Trip.Statuses.InProgress];

    public async Task<RecordResult<RecordData>> AssignAsync(Guid tripId, Guid shipmentId, CancellationToken cancellationToken) =>
        await pipeline.RunAtomicAsync(async ct =>
        {
            if (await PlanningTripAsync(tripId, ct) is { } failure)
            {
                return failure;
            }

            var shipment = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Shipment, shipmentId), ct);
            if (!shipment.IsSuccess)
            {
                return shipment;
            }

            if (Text(shipment.Value!, "status") != Shipment.Statuses.Pending)
            {
                return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Yalnızca planlama bekleyen sevkiyat sefere eklenir.");
            }

            foreach (var (type, city, address) in new[]
            {
                (TripStop.Types.Loading, "pickupCity", "pickupAddress"),
                (TripStop.Types.Unloading, "deliveryCity", "deliveryAddress"),
            })
            {
                var values = new JsonObject
                {
                    ["tripId"] = tripId.ToString(),
                    ["stopType"] = type,
                    ["city"] = shipment.Value!.Values[city]?.DeepClone(),
                    ["address"] = shipment.Value.Values[address]?.DeepClone(),
                };
                RecordResult<RecordData> stop;
                using (gate.Open(LogisticsModule.Permissions.TripsWrite, status: null, reason: null, shipmentId))
                {
                    stop = await pipeline.CreateAsync(new RecordCommand(LogisticsModule.Entities.TripStop, null, values), ct);
                }

                if (!stop.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(stop.Error!.Code, stop.Error.Message, stop.Error.FieldErrors);
                }
            }

            return await SetShipmentAsync(shipment.Value!, Shipment.Statuses.Planned, tripId, ct);
        }, cancellationToken);

    public async Task<RecordResult<RecordData>> UnassignAsync(Guid tripId, Guid shipmentId, CancellationToken cancellationToken) =>
        await pipeline.RunAtomicAsync(async ct =>
        {
            if (await PlanningTripAsync(tripId, ct) is { } failure)
            {
                return failure;
            }

            var shipment = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Shipment, shipmentId), ct);
            if (!shipment.IsSuccess)
            {
                return shipment;
            }

            if (Text(shipment.Value!, "tripId") != tripId.ToString())
            {
                return RecordResult<RecordData>.Fail(RecordErrorCode.NotFound, "Sevkiyat bu seferde değil.");
            }

            if (Text(shipment.Value!, "status") != Shipment.Statuses.Planned)
            {
                return RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Yüklemesi başlamış ya da kapanmış sevkiyat seferden çıkarılamaz.");
            }

            var stops = await context.Data.Set<TripStop>().AsNoTracking().Where(s => s.TripId == tripId && s.ShipmentId == shipmentId)
                .Select(s => new { s.Id, s.Version }).ToListAsync(ct);
            foreach (var stop in stops)
            {
                RecordResult<bool> removed;
                using (gate.Open(LogisticsModule.Permissions.TripsWrite, status: null, reason: null))
                {
                    removed = await pipeline.DeleteAsync(new RecordRef(LogisticsModule.Entities.TripStop, stop.Id), stop.Version, ct);
                }

                if (!removed.IsSuccess)
                {
                    return RecordResult<RecordData>.Fail(removed.Error!.Code, removed.Error.Message, removed.Error.FieldErrors);
                }
            }

            return await SetShipmentAsync(shipment.Value!, Shipment.Statuses.Pending, linkedTrip: null, ct);
        }, cancellationToken);

    /// <summary>Sevkiyatı sefer yazma izniyle iş akışı kapısından günceller (planlandı: sefere bağlanır; planlama bekliyor: bağ kalkar).</summary>
    internal async Task<RecordResult<RecordData>> SetShipmentAsync(RecordData shipment, string status, Guid? linkedTrip, CancellationToken cancellationToken)
    {
        using (gate.Open(LogisticsModule.Permissions.TripsWrite, status, reason: null, linkedTrip))
        {
            return await pipeline.UpdateAsync(
                new RecordCommand(LogisticsModule.Entities.Shipment, shipment.Id, new JsonObject { ["status"] = status }, shipment.Version), cancellationToken);
        }
    }

    public async Task<RecordResult<TripCheck>> CheckAsync(Guid tripId, CancellationToken cancellationToken)
    {
        var record = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Trip, tripId), cancellationToken);
        if (!record.IsSuccess)
        {
            return RecordResult<TripCheck>.Fail(record.Error!.Code, record.Error.Message, record.Error.FieldErrors);
        }

        var data = context.Data;
        var trip = await data.Set<Trip>().AsNoTracking().SingleAsync(t => t.Id == tripId, cancellationToken);
        var stops = await data.Set<TripStop>().AsNoTracking().Where(s => s.TripId == tripId).OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var shipments = await data.Set<Shipment>().AsNoTracking()
            .Where(s => s.TripId == tripId && s.Status != Shipment.Statuses.Cancelled).ToListAsync(cancellationToken);

        var issues = new List<TripCheckIssue>();
        CheckChain(trip, stops, shipments, issues);

        var vehicles = await VehiclesAsync(trip, issues, cancellationToken);
        var drivers = await DriversAsync(trip, issues, cancellationToken);
        var load = CheckLoad(stops.Count, shipments, vehicles, issues);

        if (shipments.Any(s => s.Hazardous))
        {
            issues.Add(new TripCheckIssue("hazardous", "Tehlikeli madde (ADR) yükü var: sürücünün ADR belgesini ve aracın donanımını kontrol edin.", false));
        }

        await CheckCredentialsAsync(trip, vehicles, drivers, issues, cancellationToken);
        await CheckOverlapsAsync(trip, vehicles, drivers, issues, cancellationToken);

        return RecordResult<TripCheck>.Ok(new TripCheck(issues, load));
    }

    /// <summary>Sevkiyat ve durak zinciri: en az bir sevkiyat; her sevkiyatın yükleme durağı boşaltmasından önce; taşeron değilse araç ve sürücü.</summary>
    private static void CheckChain(Trip trip, List<TripStop> stops, List<Shipment> shipments, List<TripCheckIssue> issues)
    {
        if (shipments.Count == 0)
        {
            issues.Add(new TripCheckIssue("noShipment", "Sefere en az bir sevkiyat ekleyin.", true));
        }

        if (!stops.Any(s => s.StopType == TripStop.Types.Loading) || !stops.Any(s => s.StopType == TripStop.Types.Unloading))
        {
            issues.Add(new TripCheckIssue("stops", "Seferde en az bir yükleme ve bir boşaltma durağı olmalı.", true));
        }

        foreach (var shipment in shipments)
        {
            var loading = stops.Where(s => s.ShipmentId == shipment.Id && s.StopType == TripStop.Types.Loading).Select(s => (int?)s.Sequence).Min();
            var unloading = stops.Where(s => s.ShipmentId == shipment.Id && s.StopType == TripStop.Types.Unloading).Select(s => (int?)s.Sequence).Max();
            if (loading is null || unloading is null)
            {
                issues.Add(new TripCheckIssue("shipmentStops", $"{shipment.PickupCity} → {shipment.DeliveryCity}: sevkiyatın yükleme ya da boşaltma durağı eksik.", true));
            }
            else if (unloading < loading)
            {
                issues.Add(new TripCheckIssue("stopOrder", $"{shipment.PickupCity} → {shipment.DeliveryCity}: boşaltma durağı yüklemeden önce geliyor; sırayı düzeltin.", true));
            }
        }

        if (trip.CarrierPartyId is null)
        {
            if (trip.VehicleId is null)
            {
                issues.Add(new TripCheckIssue("noVehicle", "Araç seçin (taşeron seferde taşıyıcı firmayı seçin).", true));
            }

            if (trip.DriverId is null)
            {
                issues.Add(new TripCheckIssue("noDriver", "Sürücü seçin.", true));
            }
        }
    }

    /// <summary>Araç ve dorse: Filo açık olmalı; bakımdaki ya da pasif araç sefere çıkamaz.</summary>
    private async Task<List<VehicleSummary>> VehiclesAsync(Trip trip, List<TripCheckIssue> issues, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. new[] { trip.VehicleId, trip.TrailerId }.OfType<Guid>()];
        if (ids.Length == 0)
        {
            return [];
        }

        if (!await context.Vehicles.IsAvailableAsync(cancellationToken))
        {
            issues.Add(new TripCheckIssue("fleetClosed", "Filo modülü bu firmada açık değil; araç doğrulanamıyor.", true));
            return [];
        }

        List<VehicleSummary> vehicles = [.. await context.Vehicles.GetAsync(ids, cancellationToken)];
        foreach (var vehicle in vehicles)
        {
            if (vehicle.Status == "maintenance")
            {
                issues.Add(new TripCheckIssue("vehicleMaintenance", $"{vehicle.Plate} bakımda; bakım bitince ya da başka araçla planlayın.", true));
            }
            else if (vehicle.Status == "passive")
            {
                issues.Add(new TripCheckIssue("vehiclePassive", $"{vehicle.Plate} pasif.", true));
            }
        }

        if (vehicles.Count < ids.Length)
        {
            issues.Add(new TripCheckIssue("vehicleMissing", "Seçilen araçlardan biri bulunamadı.", true));
        }

        return vehicles;
    }

    /// <summary>Sürücüler (ad soyadıyla): izindeki ya da pasif sürücü sefere çıkamaz.</summary>
    private async Task<List<(Guid Id, string Name)>> DriversAsync(Trip trip, List<TripCheckIssue> issues, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. new[] { trip.DriverId, trip.CoDriverId }.OfType<Guid>()];
        if (ids.Length == 0)
        {
            return [];
        }

        var drivers = await context.Data.Set<Driver>().AsNoTracking().Where(d => ids.Contains(d.Id))
            .Join(context.Data.Set<Person>().AsNoTracking(), d => d.PersonId, p => p.Id, (d, p) => new { d.Id, d.Status, Name = p.FirstName + " " + p.LastName })
            .ToListAsync(cancellationToken);
        foreach (var driver in drivers)
        {
            if (driver.Status == Driver.Statuses.OnLeave)
            {
                issues.Add(new TripCheckIssue("driverOnLeave", $"{driver.Name} izinde.", true));
            }
            else if (driver.Status == Driver.Statuses.Passive)
            {
                issues.Add(new TripCheckIssue("driverPassive", $"{driver.Name} pasif.", true));
            }
        }

        return [.. drivers.Select(d => (d.Id, d.Name))];
    }

    /// <summary>Kapasite: yük toplamı, aracın ve dorsenin girilmiş kapasitelerinin toplamını aşamaz (kapasitesi girilmemiş ölçü denetlenmez).</summary>
    private static TripLoad CheckLoad(int stopCount, List<Shipment> shipments, List<VehicleSummary> vehicles, List<TripCheckIssue> issues)
    {
        static T? Sum<T>(IEnumerable<T?> values)
            where T : struct, System.Numerics.INumber<T>
        {
            T[] known = [.. values.OfType<T>()];
            return known.Length == 0 ? null : known.Aggregate(T.Zero, (a, b) => a + b);
        }

        var load = new TripLoad(
            shipments.Count, stopCount,
            shipments.Sum(s => s.WeightKg ?? 0), Sum(vehicles.Select(v => v.PayloadKg)),
            shipments.Sum(s => s.PalletCount ?? 0), Sum(vehicles.Select(v => v.PalletCapacity)),
            shipments.Sum(s => s.LoadingMeters ?? 0), Sum(vehicles.Select(v => v.LoadingMeters)),
            shipments.Sum(s => s.VolumeM3 ?? 0), Sum(vehicles.Select(v => v.VolumeM3)));

        void Over(string code, string what, decimal used, decimal? capacity, string unit)
        {
            if (capacity is { } max && used > max)
            {
                issues.Add(new TripCheckIssue(code, $"{what} ({Number(used)} {unit}) araç kapasitesini ({Number(max)} {unit}) aşıyor.", true));
            }
        }

        Over("overWeight", "Yük ağırlığı", load.WeightKg, load.PayloadKg, "kg");
        Over("overPallets", "Palet sayısı", load.Pallets, load.PalletCapacity, "palet");
        Over("overLoadingMeters", "Yükleme metresi", load.LoadingMeters, load.LoadingMetersCapacity, "LDM");
        Over("overVolume", "Hacim", load.VolumeM3, load.VolumeCapacity, "m³");
        return load;
    }

    /// <summary>
    /// Süreli belgeler (ADR-0038 K3, bölüm 9 madde 3): araç, dorse ve sürücülerin her belge türünde en uzun geçerli belgesi sefer bitmeden
    /// doluyorsa bulgu olur. Ayara göre uyarı (varsayılan) ya da engel. İptal edilen belge sayılmaz; hangi türün zorunlu olduğu firma ayarı sonraki dilimde.
    /// </summary>
    private async Task CheckCredentialsAsync(
        Trip trip, List<VehicleSummary> vehicles, List<(Guid Id, string Name)> drivers, List<TripCheckIssue> issues, CancellationToken cancellationToken)
    {
        if (credentials is null)
        {
            return;
        }

        var blocking = policy is not null && await policy.CredentialCheckAsync(cancellationToken) == LogisticsModule.CredentialCheckModes.Block;
        var end = trip.PlannedEnd ?? trip.PlannedStart;
        var owners = new List<(string Key, Guid Id, string Name)>();
        owners.AddRange(vehicles.Select(v => (LogisticsLookupDefinitions.VehicleEntityKey, v.Id, v.Plate)));
        owners.AddRange(drivers.Select(d => (LogisticsModule.Entities.Driver, d.Id, d.Name)));

        foreach (var group in owners.GroupBy(o => o.Key))
        {
            var found = await credentials.ForOwnersAsync(group.Key, [.. group.Select(o => o.Id)], cancellationToken);
            foreach (var owner in group)
            {
                var latest = found.Where(c => c.OwnerId == owner.Id && c.Status == CredentialStatuses.Active)
                    .GroupBy(c => c.TypeKey)
                    .Select(g => g.OrderBy(c => c.ValidTo is null ? 0 : 1).ThenByDescending(c => c.ValidTo).First());
                foreach (var credential in latest.Where(c => c.ValidTo is { } to && to < end))
                {
                    var expired = credential.ValidTo < trip.PlannedStart;
                    issues.Add(new TripCheckIssue(
                        "credential",
                        $"{owner.Name}: {credential.TypeLabel} {Day(credential.ValidTo!.Value)} tarihinde {(expired ? "bitti" : "bitiyor (sefer bitmeden)")}.",
                        blocking));
                }
            }
        }
    }

    /// <summary>Aynı araç, dorse ya da sürücü tarih aralığı çakışan başka açık seferdeyse uyarı.</summary>
    private async Task CheckOverlapsAsync(
        Trip trip, List<VehicleSummary> vehicles, List<(Guid Id, string Name)> drivers, List<TripCheckIssue> issues, CancellationToken cancellationToken)
    {
        Guid?[] vehicleIds = [.. vehicles.Select(v => (Guid?)v.Id)];
        Guid?[] driverIds = [.. drivers.Select(d => (Guid?)d.Id)];
        if (vehicleIds.Length == 0 && driverIds.Length == 0)
        {
            return;
        }

        var start = trip.PlannedStart;
        var end = trip.PlannedEnd ?? trip.PlannedStart;
        var others = await context.Data.Set<Trip>().AsNoTracking()
            .Where(t => t.Id != trip.Id && OpenTrips.Contains(t.Status) && t.PlannedStart <= end && (t.PlannedEnd ?? t.PlannedStart) >= start)
            .Where(t => vehicleIds.Contains(t.VehicleId) || vehicleIds.Contains(t.TrailerId) || driverIds.Contains(t.DriverId) || driverIds.Contains(t.CoDriverId))
            .OrderBy(t => t.Number).Take(20).ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            IEnumerable<string> names =
            [
                .. vehicles.Where(v => v.Id == other.VehicleId || v.Id == other.TrailerId).Select(v => v.Plate),
                .. drivers.Where(d => d.Id == other.DriverId || d.Id == other.CoDriverId).Select(d => d.Name),
            ];
            issues.Add(new TripCheckIssue("overlap", $"{string.Join(", ", names)} aynı tarihlerde {other.Number} seferinde.", false));
        }
    }

    /// <summary>Sefer okunabilir, sefer yazma izni var ve sefer planlanıyor; değilse hata sonucu.</summary>
    private async Task<RecordResult<RecordData>?> PlanningTripAsync(Guid tripId, CancellationToken cancellationToken)
    {
        var trip = await pipeline.GetAsync(new RecordRef(LogisticsModule.Entities.Trip, tripId), cancellationToken);
        if (!trip.IsSuccess)
        {
            return trip;
        }

        if (!await access.HasPermissionAsync(LogisticsModule.Permissions.TripsWrite, cancellationToken))
        {
            return RecordResult<RecordData>.Fail(RecordErrorCode.PermissionDenied, "Sefer planlama yetkiniz yok.");
        }

        return Text(trip.Value!, "status") == Trip.Statuses.Planning
            ? null
            : RecordResult<RecordData>.Fail(RecordErrorCode.Conflict, "Sevkiyat yalnızca planlanan seferde eklenir ya da çıkarılır; hazır seferi önce planlamaya geri alın.");
    }

    private static string? Text(RecordData record, string field) => record.Values[field]?.GetValue<string>();

    private static string Number(decimal value) => value.ToString("#,0.##", CultureInfo.GetCultureInfo("tr-TR"));

    private static string Day(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
