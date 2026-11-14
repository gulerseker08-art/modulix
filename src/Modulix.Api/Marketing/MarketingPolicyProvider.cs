using System.Globalization;
using Modulix.Core.Contracts.Settings;
using Modulix.Modules.Marketing;
using Modulix.Modules.Marketing.Runs;

namespace Modulix.Api.Marketing;

/// <summary>Pazarlama ayarlarını tenant ayar servisinden okur (modül Core'un ayar servisini doğrudan görmez). Geçersiz değer güvenli varsayılana düşer: sağlayıcı yok, günlük sınır 1, onay zorunlu.</summary>
internal sealed class MarketingPolicyProvider(ITenantSettingReader settings) : IMarketingPolicyProvider
{
    public async Task<MarketingPolicy> GetAsync(CancellationToken cancellationToken)
    {
        var provider = await settings.GetValueAsync(MarketingModule.SettingKeys.Provider, cancellationToken);
        var limit = await settings.GetValueAsync(MarketingModule.SettingKeys.DailyLimit, cancellationToken);
        var approval = await settings.GetValueAsync(MarketingModule.SettingKeys.ApprovalRequired, cancellationToken);
        return new MarketingPolicy(
            provider == MarketingPolicy.FakeProvider ? MarketingPolicy.FakeProvider : MarketingPolicy.NoProvider,
            int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days : 1,
            !string.Equals(approval, "false", StringComparison.OrdinalIgnoreCase));
    }
}
