using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Credentials;
using Modulix.Core.Contracts.Customization;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Records;

/// <summary>
/// <see cref="ICredentialDirectory"/> uygulaması: belgeleri tenant filtresi ve RLS altında okur. Kayıt izni sormaz; çağıran modül kendi
/// kaydının (ör. sefer) iznini denetler ve yalnızca kendi kural sonucunu (uyarı/engel) gösterir.
/// </summary>
internal sealed class CredentialDirectory(ModulixDbContext db, IChoiceLists choiceLists) : ICredentialDirectory
{
    public const int MaxIds = 500;

    public async Task<IReadOnlyList<CredentialSummary>> ForOwnersAsync(
        string ownerEntityKey, IReadOnlyCollection<Guid> ownerIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerEntityKey);
        ArgumentNullException.ThrowIfNull(ownerIds);
        if (ownerIds.Count == 0)
        {
            return [];
        }

        if (ownerIds.Count > MaxIds)
        {
            throw new ArgumentException($"En çok {MaxIds} sahip kaydın belgesi birlikte okunabilir.", nameof(ownerIds));
        }

        List<Guid> ids = [.. ownerIds.Distinct()];
        var credentials = await db.Credentials.AsNoTracking()
            .Where(c => c.OwnerEntityKey == ownerEntityKey && ids.Contains(c.OwnerId))
            .OrderBy(c => c.ValidTo)
            .ToListAsync(cancellationToken);
        var types = await choiceLists.GetAsync(CredentialEntities.TypeList, cancellationToken);
        return
        [
            .. credentials.Select(c => new CredentialSummary(
                c.Id, c.OwnerEntityKey, c.OwnerId, c.TypeKey, types.Find(c.TypeKey)?.Label ?? c.TypeKey, c.DocumentNo, c.ValidFrom, c.ValidTo, c.Status)),
        ];
    }
}
