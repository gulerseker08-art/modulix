using Modulix.Core.Contracts.Communication;
using Modulix.Core.Contracts.Marketing;

namespace Modulix.Modules.Marketing.Runs;

/// <summary>
/// Pazarlama ayarları (ADR-0025, dilim 5). API host'u tenant ayar servisiyle uygular; modül Core'un ayar servisini doğrudan görmez.
/// </summary>
public sealed record MarketingPolicy(string Provider, int DailyLimit, bool ApprovalRequired)
{
    public const string NoProvider = "none";

    public const string FakeProvider = "fake";

    public bool HasProvider => Provider != NoProvider;
}

public interface IMarketingPolicyProvider
{
    Task<MarketingPolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Test sağlayıcısı: iletiyi hiçbir yere göndermez, yalnızca kabul etmiş gibi davranır. Yalnızca firma ayarında açıkça "test sağlayıcısı" seçilirse kullanılır
/// (varsayılan: sağlayıcı yok, gönderim bloklu). Adrese göre sonucu belirler, böylece geçici hata, kalıcı hata ve belirsiz sonuç gibi durumlar sınanabilir:
/// "+fail-permanent", "+fail-transient" ve "+timeout" içeren adresler sırasıyla kalıcı hata, geçici hata ve kabul sonrası kaybolan yanıt üretir.
/// Aynı anahtar her zaman aynı ileti kimliğini verir (sağlayıcı tarafı tekrar önleme).
/// </summary>
internal sealed class FakeMessageProvider : IMessageProvider
{
    public string Key => MarketingPolicy.FakeProvider;

    public bool Supports(CommunicationChannel channel) => true;

    public Task<ProviderResult> SendAsync(MessageRequest request, CancellationToken cancellationToken)
    {
        var address = request.Address.ToLowerInvariant();
        var result = address switch
        {
            _ when address.Contains("+fail-permanent", StringComparison.Ordinal) => new ProviderResult(ProviderOutcome.PermanentFailure, null, "address-rejected"),
            _ when address.Contains("+fail-transient", StringComparison.Ordinal) => new ProviderResult(ProviderOutcome.TransientFailure, null, "rate-limited"),
            _ when address.Contains("+timeout", StringComparison.Ordinal) => new ProviderResult(ProviderOutcome.Unknown, null, "timeout-after-accept"),
            _ => new ProviderResult(ProviderOutcome.Accepted, "fake-" + request.IdempotencyKey[..16].ToLowerInvariant()),
        };
        return Task.FromResult(result);
    }
}
