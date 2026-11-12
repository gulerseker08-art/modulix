using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Lojistiğin tarama kaynağı (ADR-0027 tarama tetikleyicileri, ADR-0038 dilim 4): yalnızca kayıt kimlikleri döner (kimliğe göre artan, anahtar
/// tabanlı sayfa). Zamanlanmış ve tarihe bağlı tarama yalnızca açık kayıtları kapsar: yeni talepler, teslim edilmemiş ve iptal edilmemiş
/// sevkiyatlar. Tarih alanları yükleme günü (<c>pickupDate</c>) ve en geç teslim (<c>deliveryBy</c>).
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
        _ => throw new InvalidOperationException("Bilinmeyen kayıt türü."),
    };

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
