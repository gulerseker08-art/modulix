using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// CRM kayıtlarının aktivite ilişkisi hedefleri (<see cref="IActivityLinkTarget"/>): firma, kişi, fırsat, Lead. Görünürlük, kayıt
/// boru hattının kuralıyla aynıdır (okuma izni + veri kapsamı); erişilemeyen kayıt "yok" gibi yanıtlanır ve adı verilmez.
/// </summary>
internal abstract class CrmActivityLinkTarget(IModuleData data, IRecordAccess access) : IActivityLinkTarget
{
    public abstract string EntityKey { get; }

    protected IModuleData Data => data;

    public async Task<bool> IsVisibleAsync(Guid recordId, CancellationToken cancellationToken) =>
        await access.CanAsync(EntityKey, RecordOperation.Read, cancellationToken)
        && await ExistsInScopeAsync(recordId, await access.GetOwnersAsync(cancellationToken), cancellationToken);

    public async Task<string?> GetTitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        await IsVisibleAsync(recordId, cancellationToken) ? await TitleAsync(recordId, cancellationToken) : null;

    /// <param name="owners">null: tüm kayıtlar; aksi halde yalnızca sorumlusu bu kümede olanlar.</param>
    protected abstract Task<bool> ExistsInScopeAsync(Guid recordId, IReadOnlySet<Guid>? owners, CancellationToken cancellationToken);

    protected abstract Task<string?> TitleAsync(Guid recordId, CancellationToken cancellationToken);

    protected static List<Guid?> Ids(IReadOnlySet<Guid> owners) => [.. owners.Select(id => (Guid?)id)];
}

internal sealed class CompanyLinkTarget(IModuleData data, IRecordAccess access) : CrmActivityLinkTarget(data, access)
{
    public override string EntityKey => CrmModule.Entities.Company;

    protected override Task<bool> ExistsInScopeAsync(Guid recordId, IReadOnlySet<Guid>? owners, CancellationToken cancellationToken)
    {
        var query = Data.Set<Company>().AsNoTracking().Where(c => c.Id == recordId);
        if (owners is not null)
        {
            var ids = Ids(owners);
            query = query.Where(c => ids.Contains(c.OwnerId));
        }

        return query.AnyAsync(cancellationToken);
    }

    protected override Task<string?> TitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        Data.Set<Company>().AsNoTracking().Where(c => c.Id == recordId).Select(c => c.Party.Name).SingleOrDefaultAsync(cancellationToken);
}

internal sealed class ContactLinkTarget(IModuleData data, IRecordAccess access) : CrmActivityLinkTarget(data, access)
{
    public override string EntityKey => CrmModule.Entities.Contact;

    protected override Task<bool> ExistsInScopeAsync(Guid recordId, IReadOnlySet<Guid>? owners, CancellationToken cancellationToken)
    {
        var query = Data.Set<Contact>().AsNoTracking().Where(c => c.Id == recordId);
        if (owners is not null)
        {
            query = query.Where(ContactRecordStore.VisibleTo(Data, owners));
        }

        return query.AnyAsync(cancellationToken);
    }

    protected override async Task<string?> TitleAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var person = await Data.Set<Contact>().AsNoTracking().Where(c => c.Id == recordId)
            .Select(c => new { c.Person.FirstName, c.Person.LastName }).SingleOrDefaultAsync(cancellationToken);
        return person is null ? null : $"{person.FirstName} {person.LastName}".Trim();
    }
}

internal sealed class OpportunityLinkTarget(IModuleData data, IRecordAccess access) : CrmActivityLinkTarget(data, access)
{
    public override string EntityKey => CrmModule.Entities.Opportunity;

    protected override Task<bool> ExistsInScopeAsync(Guid recordId, IReadOnlySet<Guid>? owners, CancellationToken cancellationToken)
    {
        var query = Data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == recordId);
        if (owners is not null)
        {
            var ids = Ids(owners);
            query = query.Where(o => ids.Contains(o.OwnerId));
        }

        return query.AnyAsync(cancellationToken);
    }

    // Yalnızca başlık: tutar, olasılık ve notlar aktivite üzerinden asla taşınmaz.
    protected override Task<string?> TitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        Data.Set<Opportunity>().AsNoTracking().Where(o => o.Id == recordId).Select(o => o.Title).SingleOrDefaultAsync(cancellationToken);
}

internal sealed class LeadLinkTarget(IModuleData data, IRecordAccess access, ICurrentAccess permissions) : IActivityLinkTarget
{
    public string EntityKey => CrmModule.Entities.Lead;

    public Task<bool> IsVisibleAsync(Guid recordId, CancellationToken cancellationToken) =>
        LeadAccess.IsVisibleAsync(data, access, permissions, recordId, cancellationToken);

    public async Task<string?> GetTitleAsync(Guid recordId, CancellationToken cancellationToken) =>
        await IsVisibleAsync(recordId, cancellationToken)
            ? await data.Set<Lead>().AsNoTracking().Where(l => l.Id == recordId).Select(l => l.Subject).SingleOrDefaultAsync(cancellationToken)
            : null;
}
