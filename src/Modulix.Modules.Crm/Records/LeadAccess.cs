using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Başka kayıtların Lead'e bağ kurarken kullandığı görünürlük denetimi (Lead deposunun kapsam kuralıyla aynı).</summary>
internal static class LeadAccess
{
    public static Task<bool> IsVisibleAsync(CrmStoreContext context, Guid leadId, CancellationToken cancellationToken) =>
        IsVisibleAsync(context.Data, context.Access, context.Permissions, leadId, cancellationToken);

    public static async Task<bool> IsVisibleAsync(
        IModuleData data, IRecordAccess access, ICurrentAccess permissions, Guid leadId, CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(CrmModule.Entities.Lead, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var owner = await data.Set<Lead>().AsNoTracking().Where(l => l.Id == leadId)
            .Select(l => new { l.Id, l.OwnerId }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return false;
        }

        if (await access.GetOwnersAsync(cancellationToken) is not { } scope)
        {
            return true;
        }

        return owner.OwnerId is { } ownerId
            ? scope.Contains(ownerId)
            : await permissions.HasPermissionAsync(CrmModule.Permissions.LeadsAssign, cancellationToken);
    }
}
