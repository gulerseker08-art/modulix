using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>Başka kayıtların Lead'e bağ kurarken kullandığı görünürlük denetimi (Lead deposunun kapsam kuralıyla aynı).</summary>
internal static class LeadAccess
{
    public static async Task<bool> IsVisibleAsync(CrmStoreContext context, Guid leadId, CancellationToken cancellationToken)
    {
        if (!await context.Access.CanAsync(CrmModule.Entities.Lead, RecordOperation.Read, cancellationToken))
        {
            return false;
        }

        var owner = await context.Data.Set<Lead>().AsNoTracking().Where(l => l.Id == leadId)
            .Select(l => new { l.Id, l.OwnerId }).SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return false;
        }

        if (await context.Access.GetOwnersAsync(cancellationToken) is not { } scope)
        {
            return true;
        }

        return owner.OwnerId is { } ownerId
            ? scope.Contains(ownerId)
            : await context.Permissions.HasPermissionAsync(CrmModule.Permissions.LeadsAssign, cancellationToken);
    }
}
