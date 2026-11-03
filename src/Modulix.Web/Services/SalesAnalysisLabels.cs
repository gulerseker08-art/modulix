using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>Satış analizi (teklif/lead kohortu, aşama dönüşümü, ekip performansı, veri kalitesi) ekranlarının Türkçe etiketleri. Hesap sunucudadır; burada yalnızca biçim vardır.</summary>
public static class SalesAnalysisLabels
{
    public const string NoData = "Veri yok";

    public const string NoOwner = "Sorumlusuz";

    public const string NoAmountPermission = "Yetkiniz yok";

    /// <summary>Veri kalitesi sorun kodları: ad ve kısa açıklama.</summary>
    public static IReadOnlyList<(string Key, string Label, string Help)> QualityCodes { get; } =
    [
        (DataQualityCodes.MissingAmount, "Eksik tutar", "Açık fırsatın tutarı girilmemiş; pipeline ve tahmin toplamlarına girmez."),
        (DataQualityCodes.MissingRate, "Kuru yok", "Fırsat para biriminden raporlama para birimine çeviren kur bulunamadı; toplama girmez."),
        (DataQualityCodes.MissingProbability, "Olasılık yok", "Olasılığı olmayan fırsat ağırlıklı tahmine girmez."),
        (DataQualityCodes.MissingCloseDate, "Kapanış tarihi yok", "Beklenen kapanış tarihi olmayan fırsat dönem beklentisine girmez."),
        (DataQualityCodes.Overdue, "Gecikmiş", "Beklenen kapanış tarihi geçmiş ama hâlâ açık."),
        (DataQualityCodes.Stale, "Hareketsiz", "Uzun süredir etkileşim kaydı yok."),
        (DataQualityCodes.NoHistory, "Tarihçesi yok", "Aşama tarihçesi kaydı olmayan fırsat; aşama dönüşümü ve döngü süresi analizine giremez."),
    ];

    /// <summary>Tutar iznine bağlı kodlar: izin yoksa sunucu bu denetimleri hiç üretmez (sorun yok anlamına gelmez).</summary>
    public static IReadOnlyList<string> AmountDependentCodes { get; } =
        [DataQualityCodes.MissingAmount, DataQualityCodes.MissingRate, DataQualityCodes.MissingProbability];

    public static string QualityLabel(string code) => QualityCodes.FirstOrDefault(c => c.Key == code).Label ?? code;

    public static string QualityHelp(string code) => QualityCodes.FirstOrDefault(c => c.Key == code).Help ?? string.Empty;

    /// <summary>Oran: sıfıra bölünen ya da örneklemi olmayan oran "Veri yok" görünür, sıfır gibi gösterilmez.</summary>
    public static string Rate(decimal? percent) => percent is { } value ? SalesLabels.Percent(value) : NoData;

    public static string Days(decimal? days) =>
        days is { } value ? $"{value.ToString("0.#", Regional.Numbers)} gün" : NoData;

    public static string Ratio(decimal? ratio) =>
        ratio is { } value ? $"{value.ToString("0.##", Regional.Numbers)} kat" : NoData;

    /// <summary>Sorumlu adı: atanmamış kayıt "Sorumlusuz", listede olmayan kullanıcı "Bilinmeyen kullanıcı".</summary>
    public static string Owner(CrmLookups lookups, Guid? ownerId)
    {
        if (ownerId is null)
        {
            return NoOwner;
        }

        return lookups.Members.FirstOrDefault(m => m.UserId == ownerId)?.DisplayName ?? "Bilinmeyen kullanıcı";
    }

    /// <summary>Teklif kohortunun sonuç dağılımı (aile sayıları).</summary>
    public static IReadOnlyList<(string Label, int Count)> QuoteOutcomes(QuoteCohortDto cohort) =>
    [
        ("Kabul edilen", cohort.Accepted), ("Beklemede", cohort.Pending), ("Reddedilen", cohort.Rejected),
        ("İptal edilen", cohort.Cancelled), ("Süresi dolan", cohort.Expired),
    ];

    /// <summary>Tarihçe izlemesinin başlangıcı (firma saat diliminde, tarih olarak).</summary>
    public static string TrackingStart(DateTimeOffset start) => CrmLabels.Date(DateOnly.FromDateTime(Regional.ToTenant(start).DateTime));
}
