using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Settings;

/// <summary>
/// Firmaların görünen adları (Y6b, ayar <c>core.profile.display-name</c>): üst çubuk ve firma seçimi bunu gösterir.
/// Ayarlar tenant verisidir (RLS); her firmanınki kendi bağlamında okunur.
/// </summary>
public sealed class TenantDisplayNames(ITenantScopeFactory tenantScopes)
{
    /// <summary>Görünen adı ayarlanmış firmalar → görünen ad; ayarlanmamışlar sonuçta yoktur.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetAsync(IEnumerable<Guid> tenantIds, Guid userId, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var tenantId in tenantIds.Distinct())
        {
            await using var scope = tenantScopes.Create(new TenantContext(tenantId, userId, Guid.Empty));
            var name = await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().TenantSettings.AsNoTracking()
                .Where(s => s.Key == CoreModule.SettingKeys.DisplayName)
                .Select(s => s.Value)
                .SingleOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[tenantId] = name;
            }
        }

        return names;
    }
}
