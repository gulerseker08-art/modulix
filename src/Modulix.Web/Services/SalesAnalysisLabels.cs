using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>Satış analizi (teklif/lead kohortu, aşama dönüşümü, ekip performansı, veri kalitesi) ekranlarının etiketleri (ADR-0034: sözlükten). Hesap sunucudadır; burada yalnızca biçim vardır.</summary>
public static class SalesAnalysisLabels
{
    public static string NoData => Texts.Get("crm.sales.noData");

    public static string NoOwner => Texts.Get("crm.sales.noOwner");

    public static string NoAmountPermission => Texts.Get("crm.sales.noPermission");

    public const string QualityGroup = "crm.data-quality";
    public const string QualityHelpGroup = "crm.data-quality-help";

    /// <summary>Veri kalitesi sorun kodları: ad ve kısa açıklama.</summary>
    private static readonly (string Key, string Label, string Help)[] QualitySource =
    [
        (DataQualityCodes.MissingAmount, "Eksik tutar", "Açık fırsatın tutarı girilmemiş; pipeline ve tahmin toplamlarına girmez."),
        (DataQualityCodes.MissingRate, "Kuru yok", "Fırsat para biriminden raporlama para birimine çeviren kur bulunamadı; toplama girmez."),
        (DataQualityCodes.MissingProbability, "Olasılık yok", "Olasılığı olmayan fırsat ağırlıklı tahmine girmez."),
        (DataQualityCodes.MissingCloseDate, "Kapanış tarihi yok", "Beklenen kapanış tarihi olmayan fırsat dönem beklentisine girmez."),
        (DataQualityCodes.Overdue, "Gecikmiş", "Beklenen kapanış tarihi geçmiş ama hâlâ açık."),
        (DataQualityCodes.Stale, "Hareketsiz", "Uzun süredir etkileşim kaydı yok."),
        (DataQualityCodes.NoHistory, "Tarihçesi yok", "Aşama tarihçesi kaydı olmayan fırsat; aşama dönüşümü ve döngü süresi analizine giremez."),
    ];

    /// <summary>Sorun kodlarının etkin dildeki ad ve açıklaması (ADR-0034, dilim 9a; Türkçe kaynak metindir).</summary>
    public static IReadOnlyList<(string Key, string Label, string Help)> QualityCodes =>
        [.. QualitySource.Select(c => (c.Key, CrmText.Of(QualityGroup, c.Key, c.Label), CrmText.Of(QualityHelpGroup, c.Key, c.Help)))];

    /// <summary>Tutar iznine bağlı kodlar: izin yoksa sunucu bu denetimleri hiç üretmez (sorun yok anlamına gelmez).</summary>
    public static IReadOnlyList<string> AmountDependentCodes { get; } =
        [DataQualityCodes.MissingAmount, DataQualityCodes.MissingRate, DataQualityCodes.MissingProbability];

    public static string QualityLabel(string code) => QualityCodes.FirstOrDefault(c => c.Key == code).Label ?? code;

    public static string QualityHelp(string code) => QualityCodes.FirstOrDefault(c => c.Key == code).Help ?? string.Empty;

    /// <summary>Oran: sıfıra bölünen ya da örneklemi olmayan oran "Veri yok" görünür, sıfır gibi gösterilmez.</summary>
    public static string Rate(decimal? percent) => percent is { } value ? SalesLabels.Percent(value) : NoData;

    public static string Days(decimal? days) =>
        days is { } value ? Texts.Get("crm.sales.days", value.ToString("0.#", Regional.Numbers)) : NoData;

    public static string Ratio(decimal? ratio) =>
        ratio is { } value ? Texts.Get("crm.sales.ratio", value.ToString("0.##", Regional.Numbers)) : NoData;

    /// <summary>Sorumlu adı: atanmamış kayıt "Sorumlusuz", listede olmayan kullanıcı "Bilinmeyen kullanıcı".</summary>
    public static string Owner(CrmLookups lookups, Guid? ownerId)
    {
        if (ownerId is null)
        {
            return NoOwner;
        }

        return lookups.Members.FirstOrDefault(m => m.UserId == ownerId)?.DisplayName ?? Texts.Get("crm.sales.unknownUser");
    }

    /// <summary>Teklif kohortunun sonuç dağılımı (aile sayıları).</summary>
    public static IReadOnlyList<(string Label, int Count)> QuoteOutcomes(QuoteCohortDto cohort) =>
    [
        (Texts.Get("crm.salesAnalysis.outcome.accepted"), cohort.Accepted), (Texts.Get("crm.salesAnalysis.outcome.pending"), cohort.Pending),
        (Texts.Get("crm.salesAnalysis.outcome.rejected"), cohort.Rejected), (Texts.Get("crm.salesAnalysis.outcome.cancelled"), cohort.Cancelled),
        (Texts.Get("crm.salesAnalysis.outcome.expired"), cohort.Expired),
    ];

    /// <summary>Tarihçe izlemesinin başlangıcı (firma saat diliminde, tarih olarak).</summary>
    public static string TrackingStart(DateTimeOffset start) => CrmLabels.Date(DateOnly.FromDateTime(Regional.ToTenant(start).DateTime));
}
