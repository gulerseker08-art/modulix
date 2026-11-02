using Modulix.Shared.Crm;

namespace Modulix.Web.Services;

/// <summary>
/// Satış görünümü, hedef ve tahmin ekranlarının izin anahtarları (sunucudaki <c>CrmModule.Permissions</c> ile aynı; ADR-0026).
/// Menü ve düğmeler buna göre gösterilir; asıl denetim sunucudadır.
/// </summary>
public static class SalesAccess
{
    public const string ReportsRead = CrmAccess.SalesReportsRead;
    public const string TargetsRead = CrmAccess.SalesTargetsRead;
    public const string TargetsManage = CrmAccess.SalesTargetsManage;
    public const string ForecastsRead = CrmAccess.ForecastsRead;
    public const string ForecastsWrite = CrmAccess.ForecastsWrite;
}

/// <summary>Satış raporlama ve tahmin ekranlarının Türkçe etiketleri ve gösterim yardımcıları. Hesap sunucudadır; burada yalnızca biçim vardır.</summary>
public static class SalesLabels
{
    public static IReadOnlyList<(string Key, string Label)> PeriodKinds { get; } =
        [(SalesPeriodKinds.Month, "Ay"), (SalesPeriodKinds.Quarter, "Çeyrek"), (SalesPeriodKinds.Year, "Yıl")];

    public static IReadOnlyList<(string Key, string Label)> Metrics { get; } =
        [(SalesTargetMetrics.WonAmount, "Kazanılan tutar"), (SalesTargetMetrics.WonCount, "Kazanılan adet")];

    public static IReadOnlyList<(string Key, string Label)> Scopes { get; } =
        [(SalesTargetScopes.Tenant, "Tüm firma"), (SalesTargetScopes.User, "Kullanıcı")];

    public static IReadOnlyList<(string Key, string Label)> Categories { get; } =
    [
        (ForecastCategories.Commit, "Taahhüt"), (ForecastCategories.BestCase, "En iyi durum"),
        (ForecastCategories.Pipeline, "Pipeline"), (ForecastCategories.Omitted, "Hariç"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
        [(ForecastStatuses.Draft, "Taslak"), (ForecastStatuses.Submitted, "Sunuldu"), (ForecastStatuses.Locked, "Kilitlendi")];

    public static IReadOnlyList<(string Key, string Label)> AdjustmentKinds { get; } =
        [(ForecastAdjustmentKinds.Category, "Kategori"), (ForecastAdjustmentKinds.Amount, "Tutar"), (ForecastAdjustmentKinds.Probability, "Olasılık")];

    public static IReadOnlyList<(string Key, string Label)> Cards { get; } =
    [
        (SalesCards.Realized, "Gerçekleşen"), (SalesCards.OpenPipeline, "Açık pipeline"), (SalesCards.ExpectedOpen, "Bu dönem beklenen"),
        (SalesCards.WeightedOpen, "Ağırlıklı tahmin"), (SalesCards.Lost, "Kaybedilen"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Issues { get; } =
        [("MissingAmount", "Tutar yok"), ("MissingRate", "Kur yok")];

    public static IReadOnlyList<(string Key, string Label)> ProbabilitySources { get; } =
        [("Override", "Düzeltme"), ("Manual", "Elle"), ("Stage", "Aşama varsayılanı"), ("Unknown", "Bilinmiyor")];

    public static IReadOnlyList<(string Key, string Label)> Flags { get; } =
    [
        ("MissingAmount", "Tutar yok"), ("MissingRate", "Kur yok"), ("MissingProbability", "Olasılık yok"), ("MissingCloseDate", "Kapanış tarihi yok"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Outcomes { get; } =
    [
        ("Won", "Kazanıldı"), ("Lost", "Kaybedildi"), ("Cancelled", "İptal edildi"), ("OnHold", "Beklemede"),
        ("Postponed", "Ertelendi"), ("Deleted", "Silindi"), ("Other", "Diğer"),
    ];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string PeriodKind(string key) => Label(PeriodKinds, key);

    public static string Metric(string key) => Label(Metrics, key);

    public static string Category(string key) => Label(Categories, key);

    public static string Status(string key) => Label(Statuses, key);

    public static string Source(string key) => Label(ProbabilitySources, key);

    public static string Outcome(string? key) => key is null ? "—" : Label(Outcomes, key);

    public static string Flag(string key) => Label(Flags, key);

    public static string Issue(string? key) => key is null ? string.Empty : Label(Issues, key);

    public static string CategoryBadge(string category) => category switch
    {
        ForecastCategories.Commit => "won",
        ForecastCategories.BestCase => "proposal",
        ForecastCategories.Omitted => "cancelled",
        _ => "open",
    };

    public static string StatusBadge(string status) => status switch
    {
        ForecastStatuses.Locked => "won",
        ForecastStatuses.Submitted => "proposal",
        _ => "onhold",
    };

    /// <summary>Dönemin kısa gösterimi: "01.11.2026 – 30.11.2026".</summary>
    public static string Period(DateOnly start, DateOnly end) => $"{CrmLabels.Date(start)} – {CrmLabels.Date(end)}";

    public static string Percent(decimal? value) => value is { } v ? $"%{v.ToString("0.#", Regional.Numbers)}" : "—";

    public static string Count(int value) => value.ToString("N0", Regional.Numbers);

    /// <summary>Tutar kutusunun kısmi veri açıklaması (rozetin başlığı ve ekran okuyucu metni).</summary>
    public static string PartialHint(MoneyTotalDto total)
    {
        var parts = new List<string>();
        if (total.MissingAmountCount > 0)
        {
            parts.Add($"{total.MissingAmountCount} fırsatın tutarı yok");
        }

        if (total.MissingRateCount > 0)
        {
            parts.Add($"{total.MissingRateCount} fırsatın kuru bulunamadı");
        }

        return parts.Count == 0 ? "Toplam kısmi veriyle hesaplandı." : $"Toplama girmeyenler: {string.Join(", ", parts)}.";
    }

    /// <summary>Takvim dönemi (sunucu mali yıl ayarını bilir; istemci yalnızca sunucuya ulaşılamazsa bunu kullanır).</summary>
    public static (DateOnly Start, DateOnly End) CalendarPeriod(string kind, DateOnly anchor) => kind switch
    {
        SalesPeriodKinds.Year => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 31)),
        SalesPeriodKinds.Quarter => QuarterOf(anchor),
        _ => (new DateOnly(anchor.Year, anchor.Month, 1), new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1)),
    };

    private static (DateOnly Start, DateOnly End) QuarterOf(DateOnly anchor)
    {
        var start = new DateOnly(anchor.Year, (anchor.Month - 1) / 3 * 3 + 1, 1);
        return (start, start.AddMonths(3).AddDays(-1));
    }
}
