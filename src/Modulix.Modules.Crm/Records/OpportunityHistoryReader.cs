using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Fırsat tarihçesinin salt okunur erişimi. Yazma yalnızca fırsat deposundadır (aynı işlemde); buradan hiçbir şey değiştirilmez.
/// Çağıran, fırsatın görünürlüğünü (izin ve veri kapsamı) kayıt boru hattında kendisi doğrulamış olmalıdır.
/// </summary>
internal sealed class OpportunityHistoryReader(IModuleData data)
{
    public async Task<List<OpportunityHistory>> ForAsync(Guid opportunityId, CancellationToken cancellationToken) =>
        await data.Set<OpportunityHistory>().AsNoTracking().Where(h => h.OpportunityId == opportunityId)
            .OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(cancellationToken);

    public async Task<Dictionary<Guid, List<OpportunityHistory>>> ForManyAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<OpportunityHistory>>();
        foreach (var chunk in ids.Distinct().Chunk(1000))
        {
            var rows = await data.Set<OpportunityHistory>().AsNoTracking().Where(h => chunk.Contains(h.OpportunityId))
                .OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(cancellationToken);
            foreach (var group in rows.GroupBy(h => h.OpportunityId))
            {
                result[group.Key] = [.. group];
            }
        }

        return result;
    }
}
