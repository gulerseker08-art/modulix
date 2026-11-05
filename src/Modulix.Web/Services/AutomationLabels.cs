using Modulix.Shared.Automation;

namespace Modulix.Web.Services;

/// <summary>
/// Otomasyon kuralı ekranlarının (ADR-0027, Dilim 5) Türkçe adları ve rozet tonları. Durum değerleri sunucu sözleşmesindeki
/// <c>AutomationRuleStatus</c> adlarıdır (Draft, Active, Inactive); hesap sunucudadır, burada yalnızca gösterim vardır.
/// </summary>
public static class AutomationLabels
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    public static IReadOnlyList<string> Statuses { get; } = [Draft, Active, Inactive];

    public static string Status(string status) => status switch
    {
        Draft => "Taslak",
        Active => "Açık",
        Inactive => "Kapalı",
        _ => status,
    };

    /// <summary>Rozet tonu (<c>badge stage-*</c> sınıfının eki): açık yeşil, kapalı nötr, taslak bilgi tonu.</summary>
    public static string StatusBadge(string status) => status switch
    {
        Active => "won",
        Inactive => "lead",
        _ => "proposal",
    };

    /// <summary>Kural durumu görünüm seçicisinin "Tümü" anahtarı.</summary>
    public const string AllViews = "all";

    /// <summary>Kayıt türünün kullanıcıya görünen adı; katalogda yoksa (modül kapandı, izin yok) teknik anahtar.</summary>
    public static string EntityName(IReadOnlyList<AutomationEntityDto> entities, string entityKey) =>
        entities.FirstOrDefault(e => e.EntityKey == entityKey)?.DisplayName ?? entityKey;

    /// <summary>
    /// Kuralın şu an ne yaptığı: yayınlanmış sürüm açıksa çalışır, kapalıysa durur; yalnızca taslaksa hiç çalışmamıştır. Liste ve ayrıntıda
    /// durum rozetinin yanındaki açıklama bundan gelir.
    /// </summary>
    public static string Behaviour(AutomationRuleSummaryDto rule) => rule.Status switch
    {
        Active when rule.HasDraft => "Çalışıyor; bekleyen taslak yayınlanana kadar eski sürüm geçerli.",
        Active => "Çalışıyor.",
        Inactive => "Kapalı; yayınlanmış sürüm çalışmaz.",
        _ => "Taslak; yayınlanana kadar hiçbir şey yapmaz.",
    };
}
