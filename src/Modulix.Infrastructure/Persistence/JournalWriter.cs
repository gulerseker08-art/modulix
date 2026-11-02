using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;

namespace Modulix.Infrastructure.Persistence;

/// <summary>Günlük satırı yazıcısı (<see cref="IJournalWriter"/>): yalnızca <see cref="IJournalEntity"/> eklenir; tenant filtresi ve RLS bağlam üzerinden geçerlidir.</summary>
internal sealed class JournalWriter(ModulixDbContext db) : IJournalWriter
{
    public void Add(IJournalEntity entity) => db.Add((object)entity);

    public Task SaveAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
