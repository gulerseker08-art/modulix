using System.Linq.Expressions;
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
    /// <summary>
    /// Kapsam dışı firma/kişileri gizleyen roller (ADR-0026 K8): CRM firması kapsam dışında kalan taraflar ve CRM kişisi
    /// kapsam dışında kalan kişiler, asgari kimlikle bile görünmez. CRM'de kaydı olmayan taraf/kişi (başka modüle ait) CRM
    /// kapsamıyla kısıtlanmaz. Kısıt yoksa (tüm kayıtlar, CRM okuma izni yok, gizleme kapalı) null.
    /// </summary>
    public async Task<IQueryable<Guid>?> HiddenAsync(string sharedEntityKey, CancellationToken cancellationToken)
    {
        var entityKey = sharedEntityKey switch
        {
            SharedEntities.Party or SharedEntities.PartyRelationship => CrmModule.Entities.Company,
            SharedEntities.Person => CrmModule.Entities.Contact,
            _ => null,
        };
        if (entityKey is null
            || !await access.CanAsync(entityKey, RecordOperation.Read, cancellationToken)
            || await access.GetOwnersAsync(entityKey, cancellationToken) is not { } owners
            || !await access.HidesOutOfScopeSharedRecordsAsync(cancellationToken))
        {
            return null;
        }

        if (sharedEntityKey == SharedEntities.PartyRelationship)
        {
            // İlişki kaydı firmasının kuralına uyar: kapsam dışı firmanın kayıtları gizlenir.
            var scopeOwners = owners.Select(id => (Guid?)id).ToList();
            var hiddenParties = data.Set<Company>().AsNoTracking().Where(c => c.OwnerId == null || !scopeOwners.Contains(c.OwnerId)).Select(c => c.PartyId);
            return data.Set<PartyRelationship>().AsNoTracking().Where(r => hiddenParties.Contains(r.PartyId)).Select(r => r.Id);
        }

        if (sharedEntityKey == SharedEntities.Party)
        {
            var ownerIds = owners.Select(id => (Guid?)id).ToList();
            return data.Set<Company>().AsNoTracking().Where(c => c.OwnerId == null || !ownerIds.Contains(c.OwnerId)).Select(c => c.PartyId);
        }

        var visible = ContactRecordStore.VisibleTo(data, owners);
        var hidden = Expression.Lambda<Func<Contact, bool>>(Expression.Not(visible.Body), visible.Parameters);
        return data.Set<Contact>().AsNoTracking().Where(hidden).Select(c => c.PersonId);
    }

    public async Task<IReadOnlySet<Guid>> RestrictedAsync(
        string sharedEntityKey, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var none = new HashSet<Guid>();
        var entityKey = sharedEntityKey switch
        {
            SharedEntities.Party or SharedEntities.PartyRelationship => CrmModule.Entities.Company,
            SharedEntities.Person => CrmModule.Entities.Contact,
            _ => null,
        };
        if (entityKey is null
            || !await access.CanAsync(entityKey, RecordOperation.Read, cancellationToken)
            || await access.GetOwnersAsync(entityKey, cancellationToken) is not { } owners)
        {
            return none;
        }

        var idList = ids.ToList();
        if (sharedEntityKey == SharedEntities.PartyRelationship)
        {
            var scopeOwners = owners.Select(id => (Guid?)id).ToList();
            var restrictedParties = data.Set<Company>().AsNoTracking().Where(c => c.OwnerId == null || !scopeOwners.Contains(c.OwnerId)).Select(c => c.PartyId);
            none.UnionWith(await data.Set<PartyRelationship>().AsNoTracking()
                .Where(r => idList.Contains(r.Id) && restrictedParties.Contains(r.PartyId)).Select(r => r.Id).ToListAsync(cancellationToken));
            return none;
        }

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
