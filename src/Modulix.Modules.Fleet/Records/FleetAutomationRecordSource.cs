using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Automation;
using Modulix.Modules.Fleet.Domain;

namespace Modulix.Modules.Fleet.Records;

/// <summary>
/// Filo'nun tarama kaynağı (ADR-0027 tarama tetikleyicileri, ADR-0038 dilim 1b): "bugün hangi araçlar bu tetikleyiciye uyuyor" sorusunu
/// yanıtlar ve yalnızca <b>kayıt kimlikleri</b> döner (kimliğe göre artan, anahtar tabanlı sayfa).
/// <list type="bullet">
///   <item><b>Zamanlanmış</b> tarama pasif olmayan araçları kapsar.</item>
///   <item><b>Tarihe bağlı</b> tarama durumdan bağımsız, sonraki bakım tarihi hedef güne düşen araçlardır ("bakıma 7 gün kala").</item>
/// </list>
/// Hareketsizlik taraması yoktur (araç için beyan edilmiş ilişki yok).
/// </summary>
internal sealed class FleetAutomationRecordSource(FleetStoreContext context) : IAutomationRecordSource
{
    public async Task<AutomationScanPage> ScanAsync(AutomationScanQuery query, CancellationToken cancellationToken)
    {
        if (query.EntityKey != FleetModule.Entities.Vehicle)
        {
            throw new InvalidOperationException("Bilinmeyen kayıt türü.");
        }

        var vehicles = context.Data.Set<Vehicle>().AsNoTracking();
        if (query.AfterRecordId is { } cursor)
        {
            vehicles = vehicles.Where(v => v.Id.CompareTo(cursor) > 0);
        }

        switch (query.Kind)
        {
            case AutomationScanKind.Scheduled:
                vehicles = vehicles.Where(v => v.Status != Vehicle.Statuses.Passive);
                break;

            case AutomationScanKind.DateRelative when query.Field == FleetModule.AutomationFields.NextMaintenanceDate && query.TargetDate is { } target:
                vehicles = vehicles.Where(v => v.NextMaintenanceDate == target);
                break;

            case AutomationScanKind.DateRelative:
                throw new InvalidOperationException("Desteklenmeyen tarih alanı.");

            default:
                throw new InvalidOperationException("Desteklenmeyen tarama türü.");
        }

        var ids = await vehicles.OrderBy(v => v.Id).Select(v => v.Id).Take(query.PageSize).ToListAsync(cancellationToken);
        return new AutomationScanPage(ids);
    }
}
