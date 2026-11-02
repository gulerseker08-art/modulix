using Modulix.Modules.Marketing.Domain;

namespace Modulix.Modules.Marketing.Reports;

/// <summary>Bir hedefe giden bir ileti: hangi kampanyadan, ne zaman (sıra için).</summary>
public sealed record TouchPoint(Guid TargetId, Guid CampaignId, DateTimeOffset At);

/// <summary>Rapor kuralları (saf): ilk dokunuş, "gönderildi" tanımı ve birim maliyet. Birim testle sabit girdilerle sınanır.</summary>
public static class ReportRules
{
    /// <summary>
    /// "Gönderildi": sağlayıcı iletiyi kabul etti ya da sonucu belirsiz. Kabul teslim demek değildir; belirsiz satır gönderilmiş olabilir, bu yüzden ayrıca sayılır
    /// ama dokunuş sayılır (ulaşmış olabilir).
    /// </summary>
    public static bool IsSent(string status) => status is DeliveryStatuses.Accepted or DeliveryStatuses.Unknown;

    /// <summary>Birim maliyet: maliyet ya da sayı yoksa bilinmez (null), sıfır sayıya bölünmez. İki ondalık.</summary>
    public static decimal? CostPer(decimal? cost, int count) => cost is { } amount && count > 0 ? decimal.Round(amount / count, 2) : null;

    /// <summary>
    /// İlk dokunuş: her hedef için en erken iletinin kampanyası. Aynı anda ise kampanya kimliği küçük olan seçilir (sonuç sıradan bağımsız, kararlı).
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> FirstTouch(IEnumerable<TouchPoint> touches) =>
        touches
            .GroupBy(t => t.TargetId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.At).ThenBy(t => t.CampaignId).First().CampaignId);
}
