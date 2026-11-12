using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Sefer deposu (ADR-0038 K5, dilim 5). Numara sunucuda verilir (SF-yıl-sıra). Araç ve dorse Filo'dan okunur (<see cref="Core.Contracts.Fleet.IVehicleDirectory"/>):
/// araç motorlu, dorse çekilen araç olmalı ve ikisi de pasif olmamalı; Filo kapalıysa araç seçilemez. Sürücüler pasif olamaz ve aynı kişi iki kez
/// seçilemez; ikinci sürücü birinci sürücüyle birlikte verilir. Taşeron seferin taşıyıcısı "Taşıyıcı" rollü firmadır. Durum ve not yalnızca iş
/// akışı komutuyla yazılır; sefer yalnızca "Planlanıyor"ken düzenlenir (hazır seferi önce planlamaya geri alın). Silinmez (K12).
/// </summary>
internal sealed class TripRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<Trip>(context)
{
    public const string NumberPrefix = "SF";

    public override string EntityKey => LogisticsModule.Entities.Trip;

    protected override string ReadPermission => LogisticsModule.Permissions.TripsRead;

    protected override string WritePermission => LogisticsModule.Permissions.TripsWrite;

    protected override IReadOnlyList<RecordField<Trip>> Fields { get; } =
    [
        Field.Computed<Trip>("number", t => t.Number),
        Field.Date<Trip>("plannedStart", t => t.PlannedStart, (t, v) => t.PlannedStart = v!.Value, required: true),
        Field.Date<Trip>("plannedEnd", t => t.PlannedEnd, (t, v) => t.PlannedEnd = v),
        Field.Id<Trip>("vehicleId", t => t.VehicleId, (t, v) => t.VehicleId = v),
        Field.Id<Trip>("trailerId", t => t.TrailerId, (t, v) => t.TrailerId = v),
        Field.Reference<Trip, Driver>("driverId", "Sürücü bulunamadı.", t => t.DriverId, (t, v) => t.DriverId = v),
        Field.Reference<Trip, Driver>("coDriverId", "İkinci sürücü bulunamadı.", t => t.CoDriverId, (t, v) => t.CoDriverId = v),
        Field.PartyRef<Trip>("carrierPartyId", "Taşıyıcı firma bulunamadı.", t => t.CarrierPartyId, (t, v) => t.CarrierPartyId = v),
        MoneyRules.Amount<Trip>("settlementAmount", t => t.SettlementAmount, (t, v) => t.SettlementAmount = v),
        MoneyRules.Currency<Trip>("settlementCurrency", t => t.SettlementCurrency, (t, v) => t.SettlementCurrency = v),
        Field.Text<Trip>("notes", Trip.TextMaxLength, t => t.Notes, (t, v) => t.Notes = v),
        Field.Computed<Trip>("status", t => t.Status),
        Field.Computed<Trip>("statusReason", t => t.StatusReason),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Trip>> Filters { get; } = new Dictionary<string, RecordFilter<Trip>>
    {
        ["status"] = (q, v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } keys && keys.All(Trip.Statuses.All.Contains)
            ? q.Where(t => keys.Contains(t.Status))
            : null,
        ["vehicleId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.VehicleId == id || t.TrailerId == id) : null,
        ["driverId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.DriverId == id || t.CoDriverId == id) : null,
        ["carrierPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(t => t.CarrierPartyId == id) : null,
        ["startFrom"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(t => t.PlannedStart >= day) : null,
        ["startTo"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(t => t.PlannedStart <= day) : null,
    };

    // Varsayılan: en yakın tarihli sefer önce (yeni planlananlar üstte), eşitse en yeni numara.
    protected override IOrderedQueryable<Trip> Order(IQueryable<Trip> source) =>
        source.OrderByDescending(t => t.PlannedStart).ThenByDescending(t => t.Number);

    protected override IReadOnlyCollection<string>? SortKeys { get; } = WithDescending("number", "plannedStart", "status", "createdAt");

    protected override IOrderedQueryable<Trip> Order(IQueryable<Trip> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "number" => By(source, t => t.Number, desc),
            "plannedStart" => By(source, t => t.PlannedStart, desc).ThenByDescending(t => t.Number),
            "status" => By(source, t => t.Status, desc).ThenByDescending(t => t.PlannedStart),
            "createdAt" => By(source, t => t.CreatedAt, desc),
            _ => Order(source),
        };
    }

    /// <summary>Numara ya da durak şehri.</summary>
    protected override IQueryable<Trip> Search(IQueryable<Trip> source, string term)
    {
        var byStop = Context.Data.Set<TripStop>().AsNoTracking().Where(s => s.City.Contains(term)).Select(s => s.TripId);
        return source.Where(t => t.Number.Contains(term) || byStop.Contains(t.Id));
    }

    protected override async Task AppliedAsync(Trip entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            if (Context.Gate.Status is { } status)
            {
                entity.Status = status;
                entity.StatusReason = Context.Gate.Reason;
            }
        }
        else if (!isNew && entity.Status != Trip.Statuses.Planning)
        {
            throw new RecordConflictException("Yalnızca planlanan sefer düzenlenir; hazır seferi önce planlamaya geri alın.");
        }

        var errors = new List<RecordFieldError>();
        if (entity.PlannedEnd is { } end && end < entity.PlannedStart)
        {
            errors.Add(new RecordFieldError("plannedEnd", "Bitiş günü başlangıç gününden önce olamaz."));
        }

        if (entity.CoDriverId is { } co && (entity.DriverId is null || co == entity.DriverId))
        {
            errors.Add(new RecordFieldError("coDriverId", entity.DriverId is null ? "Önce birinci sürücüyü seçin." : "İkinci sürücü birinci sürücüyle aynı olamaz."));
        }

        bool Changed(string name) => isNew || values.ContainsKey(name);

        if (entity.VehicleId is { } vehicleId && Changed("vehicleId"))
        {
            await CheckVehicleAsync("vehicleId", vehicleId, towed: false, errors, cancellationToken);
        }

        if (entity.TrailerId is { } trailerId && Changed("trailerId"))
        {
            await CheckVehicleAsync("trailerId", trailerId, towed: true, errors, cancellationToken);
        }

        foreach (var (name, driverId) in new[] { ("driverId", entity.DriverId), ("coDriverId", entity.CoDriverId) })
        {
            if (driverId is { } id && Changed(name)
                && await Context.Data.Set<Driver>().AsNoTracking().Where(d => d.Id == id).Select(d => d.Status).SingleOrDefaultAsync(cancellationToken) == Driver.Statuses.Passive)
            {
                errors.Add(new RecordFieldError(name, "Pasif sürücü sefere atanamaz."));
            }
        }

        if (entity.CarrierPartyId is { } carrier && Changed("carrierPartyId") && !await CarrierRoles.HasAsync(Context, carrier, cancellationToken))
        {
            errors.Add(new RecordFieldError("carrierPartyId", "Seçilen firma \"Taşıyıcı\" ilişki rolüne sahip değil; önce firma kaydından rolü ekleyin."));
        }

        MoneyRules.Validate(entity.SettlementAmount, entity.SettlementCurrency, "settlementAmount", "settlementCurrency", errors);
        if (entity.SettlementAmount is not null && entity.CarrierPartyId is null)
        {
            errors.Add(new RecordFieldError("settlementAmount", "Hakediş yalnızca taşıyıcı firma seçili (taşeron) seferde girilir."));
        }

        if (errors.Count > 0)
        {
            throw new RecordValidationException(errors);
        }

        if (isNew)
        {
            entity.Number = await NextNumberAsync(cancellationToken);
        }
    }

    protected override Task BeforeDeleteAsync(Trip entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException("Sefer silinmez; gerekirse iptal edin.");

    private async Task CheckVehicleAsync(string field, Guid vehicleId, bool towed, List<RecordFieldError> errors, CancellationToken cancellationToken)
    {
        if (!await Context.Vehicles.IsAvailableAsync(cancellationToken))
        {
            errors.Add(new RecordFieldError(field, "Filo modülü bu firmada açık değil; araç seçilemez."));
        }
        else if ((await Context.Vehicles.GetAsync([vehicleId], cancellationToken)).SingleOrDefault() is not { } vehicle)
        {
            errors.Add(new RecordFieldError(field, "Araç bulunamadı."));
        }
        else if ((vehicle.KindCategory == LogisticsModule.VehicleKindTowed) != towed)
        {
            errors.Add(new RecordFieldError(field, towed ? "Dorse alanına yalnızca dorse ya da römork seçilir." : "Dorse ve römork çekici olarak seçilemez; çekici ya da kamyon seçin."));
        }
        else if (vehicle.Status == "passive")
        {
            errors.Add(new RecordFieldError(field, "Pasif araç sefere atanamaz."));
        }
    }

    /// <summary>
    /// Sonraki numara: yılın en büyük sırası + 1 (SF-2026-00001). Aynı anda açılan iki sefer aynı numarayı alırsa benzersizlik dizini ikincisini
    /// reddeder; belge serisi (Ç8.1) gelince numara oraya taşınır.
    /// </summary>
    private async Task<string> NextNumberAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{NumberPrefix}-{DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture)}-";
        var last = await Context.Data.Set<Trip>().AsNoTracking().Where(t => t.Number.StartsWith(prefix))
            .OrderByDescending(t => t.Number).Select(t => t.Number).FirstOrDefaultAsync(cancellationToken);
        var next = last is not null && int.TryParse(last[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n + 1 : 1;
        return prefix + next.ToString("D5", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Sefer durağı deposu (ADR-0038 K5, dilim 5). Durak yalnızca planlanan sefere eklenir; sıra verilmezse son sıranın 10 fazlası olur, sefer
/// içinde tekildir. Plan (sıra, tür, yer, planlanan saat) yalnızca sefer planlanırken, varış saati yalnızca sefer yoldayken değişir; kapanan seferin
/// durakları değişmez. Sevkiyat durakları sevkiyat atamasıyla açılır ve sevkiyat seferden çıkarılınca kalkar; elle silinmez ve türü değişmez.
/// </summary>
internal sealed class TripStopRecordStore(LogisticsStoreContext context) : LogisticsRecordStore<TripStop>(context)
{
    public override string EntityKey => LogisticsModule.Entities.TripStop;

    protected override string ReadPermission => LogisticsModule.Permissions.TripsRead;

    protected override string WritePermission => LogisticsModule.Permissions.TripsWrite;

    protected override IReadOnlyList<RecordField<TripStop>> Fields { get; } =
    [
        Field.Reference<TripStop, Trip>("tripId", "Sefer bulunamadı.", s => s.TripId, (s, v) => s.TripId = v!.Value, required: true, createOnly: true),
        Field.Integer<TripStop>("sequence", s => s.Sequence == 0 ? null : s.Sequence, (s, v) => s.Sequence = v ?? 0, 1, TripStop.MaxSequence),
        Field.Choice<TripStop>("stopType", TripStop.Types.All, s => s.StopType, (s, v) => s.StopType = v),
        Field.Text<TripStop>("city", CargoEntity.CityMaxLength, s => s.City, (s, v) => s.City = v!, required: true),
        Field.Text<TripStop>("address", CargoEntity.AddressMaxLength, s => s.Address, (s, v) => s.Address = v),
        Field.Moment<TripStop>("plannedAt", s => s.PlannedAt, (s, v) => s.PlannedAt = v),
        Field.Moment<TripStop>("arrivedAt", s => s.ArrivedAt, (s, v) => s.ArrivedAt = v),
        Field.Text<TripStop>("notes", CargoEntity.TextMaxLength, s => s.Notes, (s, v) => s.Notes = v),
        Field.Computed<TripStop>("shipmentId", s => s.ShipmentId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TripStop>> Filters { get; } = new Dictionary<string, RecordFilter<TripStop>>
    {
        ["tripId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(s => s.TripId == id) : null,
        ["shipmentId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(s => s.ShipmentId == id) : null,
    };

    protected override IOrderedQueryable<TripStop> Order(IQueryable<TripStop> source) => source.OrderBy(s => s.TripId).ThenBy(s => s.Sequence);

    protected override IQueryable<TripStop> Search(IQueryable<TripStop> source, string term) =>
        source.Where(s => s.City.Contains(term) || (s.Address != null && s.Address.Contains(term)));

    protected override async Task AppliedAsync(TripStop entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        var trip = await Context.Data.Set<Trip>().AsNoTracking().Where(t => t.Id == entity.TripId)
            .Select(t => new { t.Status }).SingleAsync(cancellationToken);
        var stops = Context.Data.Set<TripStop>().AsNoTracking().Where(s => s.TripId == entity.TripId && s.Id != entity.Id);

        if (isNew)
        {
            if (trip.Status != Trip.Statuses.Planning)
            {
                throw new RecordConflictException("Durak yalnızca planlanan sefere eklenir; hazır seferi önce planlamaya geri alın.");
            }

            if (entity.ArrivedAt is not null)
            {
                throw new RecordValidationException([new RecordFieldError("arrivedAt", "Varış saati sefer yoldayken girilir.")]);
            }

            if (Context.Gate.IsOpen && Context.Gate.LinkedId is { } shipment)
            {
                entity.ShipmentId = shipment;
            }

            if (entity.Sequence == 0)
            {
                var last = await stops.MaxAsync(s => (int?)s.Sequence, cancellationToken) ?? 0;
                entity.Sequence = Math.Min(last + TripStop.SequenceStep, TripStop.MaxSequence);
            }
        }
        else
        {
            var before = await Context.Data.Set<TripStop>().AsNoTracking().SingleAsync(s => s.Id == entity.Id, cancellationToken);
            var planChanged = before.Sequence != entity.Sequence || before.StopType != entity.StopType || before.City != entity.City
                || before.Address != entity.Address || before.PlannedAt != entity.PlannedAt;
            var arrivalChanged = before.ArrivedAt != entity.ArrivedAt;
            var notesChanged = before.Notes != entity.Notes;

            if ((planChanged || arrivalChanged || notesChanged) && trip.Status is Trip.Statuses.Completed or Trip.Statuses.Cancelled)
            {
                throw new RecordConflictException("Kapanan seferin durakları değiştirilemez.");
            }

            if (planChanged && trip.Status != Trip.Statuses.Planning)
            {
                throw new RecordConflictException("Durak planı yalnızca sefer planlanırken değişir; hazır seferi önce planlamaya geri alın.");
            }

            if (arrivalChanged && trip.Status != Trip.Statuses.InProgress)
            {
                throw new RecordConflictException("Varış saati yalnızca yoldaki sefere girilir.");
            }

            if (before.ShipmentId is not null && before.StopType != entity.StopType)
            {
                throw new RecordConflictException("Sevkiyat durağının türü değiştirilemez.");
            }
        }

        if (entity.ArrivedAt is { } arrived && arrived > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new RecordValidationException([new RecordFieldError("arrivedAt", "Varış saati ileri bir zaman olamaz.")]);
        }

        if (await stops.AnyAsync(s => s.Sequence == entity.Sequence, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("sequence", "Bu sırada başka bir durak var; boş bir sıra numarası verin.")]);
        }
    }

    protected override async Task BeforeDeleteAsync(TripStop entity, CancellationToken cancellationToken)
    {
        var status = await Context.Data.Set<Trip>().AsNoTracking().Where(t => t.Id == entity.TripId).Select(t => t.Status).SingleAsync(cancellationToken);
        if (status != Trip.Statuses.Planning)
        {
            throw new RecordConflictException("Durak yalnızca sefer planlanırken kaldırılır.");
        }

        if (entity.ShipmentId is not null && !Context.Gate.IsOpen)
        {
            throw new RecordConflictException("Sevkiyat durağı elle kaldırılmaz; sevkiyatı seferden çıkarın.");
        }
    }
}

/// <summary>Firmanın "Taşıyıcı" ilişki rolünde olup olmadığı (taşeron sürücü ve taşeron sefer).</summary>
internal static class CarrierRoles
{
    public static async Task<bool> HasAsync(LogisticsStoreContext context, Guid partyId, CancellationToken cancellationToken)
    {
        var roles = await context.Data.Set<Party>().AsNoTracking()
            .Where(p => p.Id == partyId).Select(p => p.RelationshipRoles).SingleOrDefaultAsync(cancellationToken);
        return (roles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(LogisticsModule.RelationshipRoles.Carrier);
    }
}
