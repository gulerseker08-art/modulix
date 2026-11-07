using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Approvals;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Approvals;

/// <summary>
/// Süresi geçen onay adımlarını işler (ADR-0027 Dilim 4b). Etkin talebi olan firmalar tek platform sorgusuyla bulunmaz (talep tablosu RLS
/// altındadır); erişime açık her firma için kısa bir tur çalışır. Adım başına bir kez işlenir, tekrar turu ikinci bildirim üretmez.
/// </summary>
public sealed class ApprovalEscalationRunner(IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<Guid>> TenantIdsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ModulixDbContext db = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        return await db.Tenants.AsNoTracking().Where(t => TenantStatusRules.Operational.Contains(t.Status)).Select(t => t.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Bir firmanın süresi geçen adımlarını işler; işlenen adım sayısını döner.</summary>
    public async Task<int> RunTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ITenantScopeFactory tenantScopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var control = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        return await control.ServiceProvider.GetRequiredService<IApprovalService>().EscalateDueAsync(cancellationToken);
    }
}
