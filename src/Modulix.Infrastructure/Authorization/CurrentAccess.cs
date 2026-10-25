using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Events;
using Modulix.Core.Modules;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Aktif üyeliğin rollerinden ve tenant'ın modül haklarından geçerli izinleri hesaplar.
/// İstek başına bir kez okur (scoped); değişiklik bir sonraki istekte geçerli olur.
/// Olay abonesi çalışırken (ADR-0007, <see cref="EventHandlingScope"/>) kimlik "sistem"dir: izinler yalnızca aboneliğin
/// bildirdikleri (modülü tenant için açıksa), kayıt kapsamı tenant geneli; üyeliğin rollerine bakılmaz.
/// </summary>
internal sealed class CurrentAccess(
    ITenantContextAccessor tenantAccessor,
    ModuleCatalog catalog,
    IModuleEntitlements entitlements,
    MembershipPermissions membershipPermissions,
    EventHandlingScope handling) : ICurrentAccess
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private IReadOnlySet<string>? enabledModules;
    private IReadOnlySet<string>? permissions;

    public async Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moduleKey);
        if (tenantAccessor.Current is null)
        {
            return false;
        }

        return catalog.IsEnabled(moduleKey, await GetEnabledModulesAsync(cancellationToken));
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return (await GetPermissionsAsync(cancellationToken)).Contains(permission);
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        if (tenantAccessor.Current is not { } tenant)
        {
            return None;
        }

        if (permissions is null && handling.Subscription is { } subscriptionKey)
        {
            var declared = catalog.FindSubscription(subscriptionKey)?.Subscription.Permissions ?? [];
            permissions = catalog.EffectivePermissions(declared, await GetEnabledModulesAsync(cancellationToken));
        }

        permissions ??= (await membershipPermissions.GetAsync(tenant.MembershipId, await GetEnabledModulesAsync(cancellationToken), cancellationToken))
            .Permissions;
        return permissions;
    }

    public async Task<RecordScope> GetScopeAsync(CancellationToken cancellationToken) =>
        tenantAccessor.Current is { } tenant
            ? handling.Subscription is not null ? RecordScope.All : await membershipPermissions.GetScopeAsync(tenant.MembershipId, cancellationToken)
            : RecordScope.Own;

    private async Task<IReadOnlySet<string>> GetEnabledModulesAsync(CancellationToken cancellationToken) =>
        enabledModules ??= await entitlements.GetEnabledAsync(tenantAccessor.Current!.TenantId, cancellationToken);
}
