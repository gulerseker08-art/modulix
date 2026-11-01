using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Sipariş modülünün okuma sorguları (tenant filtresi ve RLS <see cref="IModuleData"/> üzerinden uygulanır). Görünürlük (izin ve veri
/// kapsamı) çağıran serviste kayıt boru hattıyla doğrulanır; bu sınıf yalnızca görünür siparişin ek verisini okur.
/// </summary>
internal sealed class OrderReader(IModuleData data)
{
    public async Task<List<SalesOrderEvent>> HistoryAsync(Guid orderId, CancellationToken cancellationToken) =>
        await data.Set<SalesOrderEvent>().AsNoTracking().Where(e => e.OrderId == orderId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(cancellationToken);

    /// <summary>Teklifin tüketim defteri satırları. Yazmak için izlenir (aynı istekte güncellenip sürüm denetimiyle kaydedilir).</summary>
    public async Task<List<QuoteLineConsumption>> ConsumptionsAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await data.Set<QuoteLineConsumption>().Where(c => c.QuoteId == quoteId).ToListAsync(cancellationToken);

    public Task<SalesOrder?> OrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        data.Set<SalesOrder>().AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);
}
