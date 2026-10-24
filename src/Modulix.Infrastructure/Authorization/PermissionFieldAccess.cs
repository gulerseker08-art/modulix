using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Records;

namespace Modulix.Infrastructure.Authorization;

/// <summary>
/// Hassas alanlar (Y8b): modüllerin tanımladığı alanlardan, kişinin geçerli izinleriyle göremedikleri. Yönetici tüm
/// izinlere sahip olduğu için her alanı görür. İzinler istek boyunca saklanır (<see cref="ICurrentAccess"/>).
/// </summary>
internal sealed class PermissionFieldAccess(ModuleCatalog catalog, ICurrentAccess access) : IFieldAccessService
{
    public async Task<IReadOnlySet<string>> GetHiddenFieldsAsync(TenantContext tenant, string entityKey, CancellationToken cancellationToken) =>
        catalog.HiddenFields(entityKey, await access.GetPermissionsAsync(cancellationToken));
}
