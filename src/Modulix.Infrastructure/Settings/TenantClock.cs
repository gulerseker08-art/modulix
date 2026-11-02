using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Settings;

namespace Modulix.Infrastructure.Settings;

/// <summary><see cref="ITenantClock"/>: saat dilimi firma ayarından (<c>core.regional.time-zone</c>) gelir, ayar yoksa İstanbul.</summary>
internal sealed class TenantClock(ITenantSettings settings, TimeProvider time) : ITenantClock
{
    public async Task<TenantDay> GetAsync(CancellationToken cancellationToken)
    {
        var zone = RegionalTime.Find(await settings.GetValueAsync(CoreModule.SettingKeys.TimeZone, cancellationToken));
        return new TenantDay(time.GetUtcNow(), zone);
    }
}
