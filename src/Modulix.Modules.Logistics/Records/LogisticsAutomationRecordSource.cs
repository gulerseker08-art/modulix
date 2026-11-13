using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Lojistiğin tarama kaynağı (ADR-0027 tarama tetikleyicileri, ADR-0038 dilim 4): yalnızca kayıt kimlikleri döner (kimliğe göre artan, anahtar
/// tabanlı sayfa). Zamanlanmış ve tarihe bağlı tarama yalnızca açık kayıtları kapsar: yeni talepler, teslim edilmemiş ve iptal edilmemiş
/// sevkiyatlar, kapanmamış seferler (dilim 5). Tarih alanları yükleme günü (<c>pickupDate</c>), en geç teslim (<c>deliveryBy</c>), seferin
/// planlanan başlangıç ve bitişi (<c>plannedStart</c>, <c>plannedEnd</c>).
/// </summary>
internal sealed class LogisticsAutomationRecordSource(LogisticsStoreContext context) : IAutomationRecordSource
{
    private static readonly string[] OpenShipments = [Shipment.Statuses.Pending, Shipment.Statuses.Planned, Shipment.Statuses.Loading, Shipment.Statuses.InTransit];

    public Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken) => query.EntityKey switch
    {
        LogisticsModule.Entities.TransportRequest =>
            ScanAsync(context.Data.Set<TransportRequest>().AsNoTracking().Where(r => r.Status == TransportRequest.Statuses.New), query, cancellationToken),
        LogisticsModule.Entities.Shipment =>
            ScanAsync(context.Data.Set<Shipment>().AsNoTracking().Where(s => OpenShipments.Contains(s.Status)), query, cancellationToken),
        LogisticsModule.Entities.Trip => ScanTripsAsync(query, cancellationToken),
        LogisticsModule.Entities.TripExpense => ScanExpensesAsync(query, cancellationToken),
        _ => throw new InvalidOperationException("Bilinmeyen kayıt türü."),
    };

    /// <summary>Karar bekleyen masraflar ("3 gündür onay bekleyen masraf" hatırlatması); tarih alanı masraf tarihidir.</summary>
    private async Task<AutomationScanPage> ScanExpensesAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        var expenses = context.Data.Set<TripExpense>().AsNoTracking().Where(e => e.Status == TripExpense.Statuses.Pending);
        if (query.AfterRecordId is { } cursor)
        {
            expenses = expenses.Where(e => e.Id.CompareTo(cursor) > 0);
        }

        expenses = query.Kind switch
        {
            AutomationScanKind.Scheduled => expenses,
            AutomationScanKind.DateRelative when query.Field == "expenseDate" && query.TargetDate is { } target => expenses.Where(e => e.ExpenseDate == target),
            AutomationScanKind.DateRelative => throw new InvalidOperationException("Desteklenmeyen tarih alanı."),
            _ => throw new InvalidOperationException("Desteklenmeyen tarama türü."),
        };

        return new AutomationScanPage(await expenses.OrderBy(e => e.Id).Select(e => e.Id).Take(query.PageSize).ToListAsync(cancellationToken));
    }

    private static readonly string[] OpenTrips = [Trip.Statuses.Planning, Trip.Statuses.Ready, Trip.Statuses.InProgress];

    /// <summary>Açık seferler (planlanan, hazır, yolda); tarih alanları planlanan başlangıç ve bitiş.</summary>
    private async Task<AutomationScanPage> ScanTripsAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        var trips = context.Data.Set<Trip>().AsNoTracking().Where(t => OpenTrips.Contains(t.Status));
        if (query.AfterRecordId is { } cursor)
        {
            trips = trips.Where(t => t.Id.CompareTo(cursor) > 0);
        }

        trips = query.Kind switch
        {
            AutomationScanKind.Scheduled => trips,
            AutomationScanKind.DateRelative when query.Field == "plannedStart" && query.TargetDate is { } target => trips.Where(t => t.PlannedStart == target),
            AutomationScanKind.DateRelative when query.Field == "plannedEnd" && query.TargetDate is { } target => trips.Where(t => t.PlannedEnd == target),
            AutomationScanKind.DateRelative => throw new InvalidOperationException("Desteklenmeyen tarih alanı."),
            _ => throw new InvalidOperationException("Desteklenmeyen tarama türü."),
        };

        return new AutomationScanPage(await trips.OrderBy(t => t.Id).Select(t => t.Id).Take(query.PageSize).ToListAsync(cancellationToken));
    }

    private static async Task<AutomationScanPage> ScanAsync<T>(IQueryable<T> records, AutomationScanQuery query, CancellationToken cancellationToken)
        where T : CargoEntity
    {
        if (query.AfterRecordId is { } cursor)
        {
            records = records.Where(r => r.Id.CompareTo(cursor) > 0);
        }

        records = query.Kind switch
        {
            AutomationScanKind.Scheduled => records,
            AutomationScanKind.DateRelative when query.Field == "pickupDate" && query.TargetDate is { } target => records.Where(r => r.PickupDate == target),
            AutomationScanKind.DateRelative when query.Field == "deliveryBy" && query.TargetDate is { } target => records.Where(r => r.DeliveryBy == target),
            AutomationScanKind.DateRelative => throw new InvalidOperationException("Desteklenmeyen tarih alanı."),
            _ => throw new InvalidOperationException("Desteklenmeyen tarama türü."),
        };

        var ids = await records.OrderBy(r => r.Id).Select(r => r.Id).Take(query.PageSize).ToListAsync(cancellationToken);
        return new AutomationScanPage(ids);
    }
}
