using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Siparişin aktivite ilişkisi hedefi (<see cref="IActivityLinkTarget"/>, ADR-0015): devir, eksik bilgi ve gecikme görevleri siparişe
/// bağlanabilir. Görünürlük kayıt boru hattının kuralıyla aynıdır (okuma izni + veri kapsamı); erişilemeyen sipariş "yok" gibi yanıtlanır.
/// Başlık yalnızca başlık ve numaradır: tutar ve ticari bilgi aktivite üzerinden taşınmaz.
/// </summary>
internal sealed class OrderLinkTarget(IModuleData data, IRecordAccess access) : IActivityLinkTarget
{
    public string EntityKey => OrdersModule.Entities.Order;

    public async Task<bool> IsVisibleAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(EntityKey, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var query = data.Set<SalesOrder>().AsNoTracking().Where(c => c.Id == recordId);
        if (await access.GetOwnersAsync(EntityKey, cancellationToken) is { } owners)
        {
            var ids = owners.Select(id => (Guid?)id).ToList();
            query = query.Where(c => ids.Contains(c.OwnerId));
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<string?> GetTitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        await IsVisibleAsync(recordId, cancellationToken)
            ? await data.Set<SalesOrder>().AsNoTracking().Where(c => c.Id == recordId).Select(c => c.Title + " (" + c.Number + ")").SingleOrDefaultAsync(cancellationToken)
            : null;
}
