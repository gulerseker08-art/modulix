using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Sectors;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Sectors;

/// <summary>Modüllerin faaliyet kataloğunu okuma sınırı (<see cref="IActivityCodeCatalog"/>): yalnızca kod, ad, tür ve etkinlik döner.</summary>
internal sealed class ActivityCodeCatalog(ModulixDbContext db) : IActivityCodeCatalog
{
    public async Task<IReadOnlyList<ActivityCodeInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return [];
        }

        var nodes = await db.ActivityNodes.AsNoTracking().Where(n => codes.Contains(n.Code))
            .Select(n => new { n.Code, n.Name, n.Kind, n.IsActive }).ToListAsync(cancellationToken);
        return [.. nodes.Select(n => new ActivityCodeInfo(n.Code, n.Name, n.Kind.ToString(), n.IsActive))];
    }
}
