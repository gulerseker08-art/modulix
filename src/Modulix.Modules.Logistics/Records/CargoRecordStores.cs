using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Taşıma talebi ve sevkiyatın ortak deposu (ADR-0038 K5–K7): müşteri, gönderen, alıcı, taşıma türü, güzergâh, tarih penceresi ve yük. Durum ve
/// gerekçe yalnızca iş akışı kapısı açıkken yazılır (<see cref="LogisticsWorkflowGate"/>); genel uçtan gelen değer yok sayılır. Kayıtlar silinmez
/// (K12). Ortak kurallar: yükleme ve boşaltma şehri, yük tanımı zorunlu; teslim günü yükleme gününden önce olamaz; düzenleme yalnızca
/// <see cref="EditableStatuses"/> durumlarında.
/// </summary>
internal abstract class CargoRecordStore<TEntity>(LogisticsStoreContext context) : LogisticsRecordStore<TEntity>(context)
    where TEntity : CargoEntity, new()
{
    /// <summary>Genel uçtan düzenlenebilen durumlar; diğerlerinde kayıt yalnızca iş akışıyla değişir.</summary>
    protected abstract IReadOnlyCollection<string> EditableStatuses { get; }

    protected abstract string LockedMessage { get; }

    protected abstract string DeleteMessage { get; }

    protected static IEnumerable<RecordField<TEntity>> CargoFields() =>
    [
        Field.PartyRef<TEntity>("customerPartyId", "Müşteri firma bulunamadı.", e => e.CustomerPartyId, (e, v) => e.CustomerPartyId = v!.Value, required: true),
        Field.PartyRef<TEntity>("senderPartyId", "Gönderen firma bulunamadı.", e => e.SenderPartyId, (e, v) => e.SenderPartyId = v),
        Field.PartyRef<TEntity>("receiverPartyId", "Alıcı firma bulunamadı.", e => e.ReceiverPartyId, (e, v) => e.ReceiverPartyId = v),
        Field.ListChoice<TEntity>("transportModeKey", LogisticsModule.Lists.TransportMode, e => e.TransportModeKey, (e, v) => e.TransportModeKey = v!),
        Field.Text<TEntity>("pickupCity", CargoEntity.CityMaxLength, e => e.PickupCity, (e, v) => e.PickupCity = v!, required: true),
        Field.Text<TEntity>("pickupAddress", CargoEntity.AddressMaxLength, e => e.PickupAddress, (e, v) => e.PickupAddress = v),
        Field.Text<TEntity>("deliveryCity", CargoEntity.CityMaxLength, e => e.DeliveryCity, (e, v) => e.DeliveryCity = v!, required: true),
        Field.Text<TEntity>("deliveryAddress", CargoEntity.AddressMaxLength, e => e.DeliveryAddress, (e, v) => e.DeliveryAddress = v),
        Field.Date<TEntity>("pickupDate", e => e.PickupDate, (e, v) => e.PickupDate = v!.Value, required: true),
        Field.Date<TEntity>("deliveryBy", e => e.DeliveryBy, (e, v) => e.DeliveryBy = v),
        Field.Text<TEntity>("cargoDescription", CargoEntity.TextMaxLength, e => e.CargoDescription, (e, v) => e.CargoDescription = v!, required: true),
        Field.Decimal<TEntity>("weightKg", e => e.WeightKg, (e, v) => e.WeightKg = v, 2, 0, 1_000_000),
        Field.Decimal<TEntity>("volumeM3", e => e.VolumeM3, (e, v) => e.VolumeM3 = v, 2, 0, 10_000),
        Field.Integer<TEntity>("palletCount", e => e.PalletCount, (e, v) => e.PalletCount = v, 0, 10_000),
        Field.Decimal<TEntity>("loadingMeters", e => e.LoadingMeters, (e, v) => e.LoadingMeters = v, 2, 0, 100),
        Field.Flag<TEntity>("hazardous", e => e.Hazardous, (e, v) => e.Hazardous = v),
        Field.Text<TEntity>("notes", CargoEntity.TextMaxLength, e => e.Notes, (e, v) => e.Notes = v),
        Field.Computed<TEntity>("status", e => e.Status),
        Field.Computed<TEntity>("statusReason", e => e.StatusReason),
    ];

    protected static Dictionary<string, RecordFilter<TEntity>> CargoFilters(IReadOnlyCollection<string> statuses) => new()
    {
        ["status"] = (q, v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } keys && keys.All(statuses.Contains)
            ? q.Where(e => keys.Contains(e.Status))
            : null,
        ["customerPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.CustomerPartyId == id) : null,
        ["senderPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.SenderPartyId == id) : null,
        ["receiverPartyId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(e => e.ReceiverPartyId == id) : null,
        ["transportModeKey"] = (q, v) => q.Where(e => e.TransportModeKey == v),
        ["pickupFrom"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.PickupDate >= day) : null,
        ["pickupTo"] = (q, v) => FilterValue.Day(v) is { } day ? q.Where(e => e.PickupDate <= day) : null,
    };

    private IQueryable<Party> Parties => Context.Data.Set<Party>().AsNoTracking();

    // Varsayılan: yükleme günü en yakın olan önce, sonra en yeni kayıt.
    protected override IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source) =>
        source.OrderBy(e => e.PickupDate).ThenByDescending(e => e.CreatedAt);

    protected override IReadOnlyCollection<string>? SortKeys { get; } =
        WithDescending("pickupDate", "deliveryBy", "customer", "pickupCity", "deliveryCity", "status", "createdAt");

    protected override IOrderedQueryable<TEntity> Order(IQueryable<TEntity> source, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        return sort?.TrimStart('-') switch
        {
            "pickupDate" => By(source, e => e.PickupDate, desc).ThenByDescending(e => e.CreatedAt),
            "deliveryBy" => ByNullsLast(source, e => e.DeliveryBy, desc).ThenBy(e => e.PickupDate),
            "customer" => By(source, e => Parties.Where(p => p.Id == e.CustomerPartyId).Select(p => p.Name).FirstOrDefault(), desc).ThenBy(e => e.PickupDate),
            "pickupCity" => By(source, e => e.PickupCity, desc).ThenBy(e => e.PickupDate),
            "deliveryCity" => By(source, e => e.DeliveryCity, desc).ThenBy(e => e.PickupDate),
            "status" => By(source, e => e.Status, desc).ThenBy(e => e.PickupDate),
            "createdAt" => By(source, e => e.CreatedAt, desc),
            _ => Order(source),
        };
    }

    protected override IQueryable<TEntity> Search(IQueryable<TEntity> source, string term)
    {
        var customers = Parties.Where(p => p.Name.Contains(term)).Select(p => p.Id);
        return source.Where(e => customers.Contains(e.CustomerPartyId) || e.PickupCity.Contains(term) || e.DeliveryCity.Contains(term)
            || e.CargoDescription.Contains(term));
    }

    protected override async Task AppliedAsync(TEntity entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen)
        {
            if (Context.Gate.Status is { } status)
            {
                entity.Status = status;
                entity.StatusReason = Context.Gate.Reason;
            }
        }
        else if (!isNew && !EditableStatuses.Contains(entity.Status))
        {
            throw new RecordConflictException(LockedMessage);
        }

        if (entity.DeliveryBy is { } by && by < entity.PickupDate)
        {
            throw new RecordValidationException([new RecordFieldError("deliveryBy", "Teslim günü yükleme gününden önce olamaz.")]);
        }

        if (entity.CustomerPartyId == Guid.Empty)
        {
            throw new RecordValidationException([new RecordFieldError("customerPartyId", "Bu alan zorunludur.")]);
        }

        await CargoAppliedAsync(entity, values, isNew, cancellationToken);
    }

    /// <summary>Türe özgü kurallar (ortak kurallardan sonra).</summary>
    protected virtual Task CargoAppliedAsync(TEntity entity, JsonObject values, bool isNew, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task BeforeDeleteAsync(TEntity entity, CancellationToken cancellationToken) =>
        throw new RecordConflictException(DeleteMessage);
}

/// <summary>
/// Taşıma talebi deposu (ADR-0038 K6, dilim 4). Elle (kaynak <c>manual</c>) ya da API'den (<c>api</c>) açılır; dış referans kaynak içinde tekildir.
/// Yalnızca "Yeni" talep düzenlenir; onay, ret ve iptal iş akışı komutudur (<see cref="Lifecycle.LogisticsWorkflow"/>).
/// </summary>
internal sealed class TransportRequestRecordStore(LogisticsStoreContext context) : CargoRecordStore<TransportRequest>(context)
{
    public override string EntityKey => LogisticsModule.Entities.TransportRequest;

    protected override string ReadPermission => LogisticsModule.Permissions.RequestsRead;

    protected override string WritePermission => LogisticsModule.Permissions.RequestsWrite;

    protected override IReadOnlyCollection<string> EditableStatuses { get; } = [TransportRequest.Statuses.New];

    protected override string LockedMessage => "Onaylanan, reddedilen ya da iptal edilen talep değiştirilemez.";

    protected override string DeleteMessage => "Taşıma talebi silinmez; gerekirse iptal edin.";

    protected override IReadOnlyList<RecordField<TransportRequest>> Fields { get; } =
    [
        .. CargoFields(),
        Field.Choice<TransportRequest>("source", TransportRequest.Sources.All, r => r.Source, (r, v) => r.Source = v, createOnly: true),
        Field.Text<TransportRequest>("externalRef", TransportRequest.ExternalRefMaxLength, r => r.ExternalRef, (r, v) => r.ExternalRef = v, createOnly: true),
        Field.Computed<TransportRequest>("shipmentId", r => r.ShipmentId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<TransportRequest>> Filters { get; } = new Dictionary<string, RecordFilter<TransportRequest>>(
        CargoFilters(TransportRequest.Statuses.All))
    {
        ["source"] = (q, v) => TransportRequest.Sources.All.Contains(v) ? q.Where(r => r.Source == v) : null,
        ["externalRef"] = (q, v) => q.Where(r => r.ExternalRef == v),
    };

    protected override async Task CargoAppliedAsync(TransportRequest entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        if (Context.Gate.IsOpen && Context.Gate.LinkedId is { } shipment)
        {
            entity.ShipmentId = shipment;
        }

        if (entity.ExternalRef is { } reference && await Context.Data.Set<TransportRequest>().AsNoTracking()
                .AnyAsync(r => r.Id != entity.Id && r.Source == entity.Source && r.ExternalRef == reference, cancellationToken))
        {
            throw new RecordValidationException([new RecordFieldError("externalRef", "Bu referansla açılmış bir talep zaten var.")]);
        }
    }
}

/// <summary>
/// Sevkiyat deposu (ADR-0038 K5, K7, dilim 4). Genel uçtan oluşturulamaz: sevkiyat yalnızca talep onayıyla, iş akışı kapısı açıkken doğar.
/// Yükleme başlayana kadar (Planlama bekliyor, Planlandı) yük ve tarih alanları düzeltilebilir; durum iş akışı komutudur. Planlama sefer atamasıdır
/// (dilim 5): atanan sevkiyat "Planlandı" olur ve sefere bağlanır, seferden çıkarılan "Planlama bekliyor"a döner.
/// </summary>
internal sealed class ShipmentRecordStore(LogisticsStoreContext context) : CargoRecordStore<Shipment>(context)
{
    public override string EntityKey => LogisticsModule.Entities.Shipment;

    protected override string ReadPermission => LogisticsModule.Permissions.ShipmentsRead;

    protected override string WritePermission => LogisticsModule.Permissions.ShipmentsWrite;

    protected override IReadOnlyCollection<string> EditableStatuses { get; } = [Shipment.Statuses.Pending, Shipment.Statuses.Planned];

    protected override string LockedMessage => "Yüklemesi başlamış, teslim edilmiş ya da iptal edilmiş sevkiyatın yük bilgisi değiştirilemez.";

    protected override string DeleteMessage => "Sevkiyat silinmez; gerekirse iptal edin.";

    protected override string? CreateBlockedMessage => Context.Gate.IsOpen ? null : "Sevkiyat yalnızca taşıma talebi onaylanınca oluşur.";

    protected override IReadOnlyList<RecordField<Shipment>> Fields { get; } =
    [
        .. CargoFields(),
        Field.Reference<Shipment, TransportRequest>("requestId", "Taşıma talebi bulunamadı.", s => s.RequestId, (s, v) => s.RequestId = v!.Value, required: true, createOnly: true),
        Field.Computed<Shipment>("tripId", s => s.TripId?.ToString()),
    ];

    protected override IReadOnlyDictionary<string, RecordFilter<Shipment>> Filters { get; } = new Dictionary<string, RecordFilter<Shipment>>(
        CargoFilters(Shipment.Statuses.All))
    {
        ["requestId"] = (q, v) => FilterValue.Id(v) is { } id ? q.Where(s => s.RequestId == id) : null,
        ["tripId"] = (q, v) => v == "none" ? q.Where(s => s.TripId == null) : FilterValue.Id(v) is { } id ? q.Where(s => s.TripId == id) : null,
    };

    protected override async Task CargoAppliedAsync(Shipment entity, JsonObject values, bool isNew, CancellationToken cancellationToken)
    {
        // Sefer ataması (dilim 5): "Planlandı"ya geçiş sefere bağlar, "Planlama bekliyor"a dönüş bağı kaldırır.
        if (Context.Gate.IsOpen && Context.Gate.Status == Shipment.Statuses.Planned)
        {
            entity.TripId = Context.Gate.LinkedId;
        }
        else if (Context.Gate.IsOpen && Context.Gate.Status == Shipment.Statuses.Pending)
        {
            entity.TripId = null;
        }

        if (isNew && (entity.RequestId == Guid.Empty
            || await Context.Data.Set<Shipment>().AsNoTracking().AnyAsync(s => s.RequestId == entity.RequestId, cancellationToken)))
        {
            throw new RecordConflictException("Bu talep için sevkiyat zaten açılmış.");
        }
    }
}
