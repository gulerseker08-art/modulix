using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM'in ortak kayıt kısıtı (ADR-0010): çağıranın CRM'deki veri kapsamı dışında kalan firma ve kişiler genel uçlardan
/// (<c>core.party</c>, <c>core.person</c>) yalnızca asgari kimlik bilgisiyle görünür. CRM okuma izni olmayan çağıranın
/// erişimi başka bir modülden geldiği için kısıt bildirilmez.
/// </summary>
internal sealed class CrmSharedRecordVisibility(IModuleData data, IRecordAccess access) : ISharedRecordVisibility
{
    public async Task<IReadOnlySet<Guid>> RestrictedAsync(
        string sharedEntityKey, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var none = new HashSet<Guid>();
        var entityKey = sharedEntityKey switch
        {
            SharedEntities.Party => CrmModule.Entities.Company,
            SharedEntities.Person => CrmModule.Entities.Contact,
            _ => null,
        };
        if (entityKey is null
            || !await access.CanAsync(entityKey, RecordOperation.Read, cancellationToken)
            || await access.GetOwnersAsync(cancellationToken) is not { } owners)
        {
            return none;
        }

        var idList = ids.ToList();
        if (sharedEntityKey == SharedEntities.Party)
        {
            var ownerIds = owners.Select(id => (Guid?)id).ToList();
            var all = await data.Set<Company>().AsNoTracking().Where(c => idList.Contains(c.PartyId)).Select(c => c.PartyId).ToListAsync(cancellationToken);
            var visible = await data.Set<Company>().AsNoTracking()
                .Where(c => idList.Contains(c.PartyId) && ownerIds.Contains(c.OwnerId)).Select(c => c.PartyId).ToListAsync(cancellationToken);
            none.UnionWith(all.Except(visible));
            return none;
        }

        var contacts = await data.Set<Contact>().AsNoTracking().Where(c => idList.Contains(c.PersonId)).Select(c => c.PersonId).ToListAsync(cancellationToken);
        var visibleContacts = await data.Set<Contact>().AsNoTracking()
            .Where(c => idList.Contains(c.PersonId)).Where(ContactRecordStore.VisibleTo(data, owners)).Select(c => c.PersonId)
            .ToListAsync(cancellationToken);
        none.UnionWith(contacts.Except(visibleContacts));
        return none;
    }
}
