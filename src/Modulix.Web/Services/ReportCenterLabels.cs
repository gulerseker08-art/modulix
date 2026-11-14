using System.Globalization;
using System.Text.Json;
using Modulix.Shared.Reporting;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 15); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Tablolar özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir). Yeni değer eklenince
// üç sözlüğe de anahtar eklenir (`ReportCenterLabelsTests` kapsamı denetler).

/// <summary>
/// Rapor merkezinin (ADR-0035 dilim 3) etiket ve biçim yardımcıları. Sabit değerler sunucu sözleşmesindeki camelCase metinlerdir
/// (<c>ReportContracts.cs</c> baş yorumu); burada yalnızca adları ve gösterim biçimleri vardır, hesap sunucudadır.
/// (Destek raporlarının yardımcıları <see cref="ReportLabels"/>'tadır.)
/// </summary>
public static class ReportCenterLabels
{
    public const string Table = "table";
    public const string Summary = "summary";

    public const string OperatorGroup = "reports.operator";
    public const string AggregationGroup = "reports.aggregation";
    public const string ChartGroup = "reports.chart";
    public const string GrainGroup = "reports.grain";
    public const string VisibilityGroup = "reports.visibility";
    public const string VisibilityBadgeGroup = "reports.visibility-badge";
    public const string DirectionGroup = "reports.direction";
    public const string HealthGroup = "reports.health";
    public const string KindGroup = "reports.kind";
    public const string FrequencyGroup = "reports.frequency";
    public const string WeekdayGroup = "reports.weekday";
    public const string ExportSourceGroup = "reports.export-source";
    public const string ScheduleStatusGroup = "reports.schedule-status";
    public const string DaySuffixGroup = "reports.day-suffix";

    public static string KeyOf(string group, string key) => $"{group}.{key.Replace(':', '-').ToLowerInvariant()}";

    public static string Of(string group, string key, string fallback) => Texts.GetOr(KeyOf(group, key), fallback);

    /// <summary>Tabloyu her okunuşta çevirir.</summary>
    public static IReadOnlyList<(string Key, string Label)> Translate(string group, IReadOnlyList<(string Key, string Label)> source) =>
        [.. source.Select(x => (x.Key, Of(group, x.Key, x.Label)))];

    /// <summary>Tablodaki karşılığı çevirir; tabloda olmayan anahtar olduğu gibi (boşsa boş metin) döner.</summary>
    private static string Lookup(string group, IReadOnlyList<(string Key, string Label)> source, string? key)
    {
        foreach (var (k, label) in source)
        {
            if (k == key)
            {
                return Of(group, k, label);
            }
        }

        return key ?? string.Empty;
    }

    internal static readonly (string Key, string Label)[] OperatorSource =
    [
        ("equals", "eşittir"), ("notEquals", "eşit değildir"), ("greater", "büyüktür"), ("greaterOrEqual", "büyük veya eşittir"),
        ("less", "küçüktür"), ("lessOrEqual", "küçük veya eşittir"), ("contains", "içerir"), ("in", "şunlardan biri"),
        ("isEmpty", "boştur"), ("notEmpty", "boş değildir"),
    ];

    internal static readonly (string Key, string Label)[] AggregationSource =
        [("sum", "Toplam"), ("average", "Ortalama"), ("min", "En düşük"), ("max", "En yüksek")];

    internal static readonly (string Key, string Label)[] ChartSource =
        [("none", "Grafik yok"), ("bar", "Çubuk"), ("line", "Çizgi"), ("pie", "Pasta")];

    internal static readonly (string Key, string Label)[] GrainSource = [("day", "Gün"), ("month", "Ay"), ("year", "Yıl")];

    internal static readonly (string Key, string Label)[] VisibilitySource =
    [
        ("private", "Yalnızca ben"), ("users", "Seçtiğim kullanıcılar"), ("tenant", "Firmadaki herkes"),
    ];

    /// <summary>Paylaşım rozetinin kısa adları (özel / seçilen kullanıcılar / firma).</summary>
    internal static readonly (string Key, string Label)[] VisibilityBadgeSource =
        [("private", "Özel"), ("users", "Seçilen kullanıcılar"), ("tenant", "Firma")];

    internal static readonly (string Key, string Label)[] DirectionSource = [("ascending", "Artan"), ("descending", "Azalan")];

    internal static readonly (string Key, string Label)[] HealthSource =
    [
        ("ok", "Çalışır"), ("datasetMissing", "Veri kümesi yok"), ("moduleDisabled", "Modül etkin değil"), ("forbidden", "Yetkiniz yok"),
        ("columnsInvalid", "Sütun geçersiz"),
    ];

    internal static readonly (string Key, string Label)[] KindSource =
    [
        ("text", "Metin"), ("number", "Sayı"), ("money", "Tutar"), ("date", "Tarih"), ("dateTime", "Tarih ve saat"), ("flag", "Evet/Hayır"),
        ("choice", "Seçim"), ("lookup", "Kayıt"), ("user", "Kullanıcı"),
    ];

    internal static readonly (string Key, string Label)[] FrequencySource = [("daily", "Her gün"), ("weekly", "Her hafta"), ("monthly", "Her ay")];

    /// <summary>Haftanın günleri Pazartesi'den başlar (Türkiye); anahtarlar sözleşmedeki sunday..saturday'dir.</summary>
    internal static readonly (string Key, string Label)[] WeekdaySource =
    [
        ("monday", "pazartesi"), ("tuesday", "salı"), ("wednesday", "çarşamba"), ("thursday", "perşembe"),
        ("friday", "cuma"), ("saturday", "cumartesi"), ("sunday", "pazar"),
    ];

    internal static readonly (string Key, string Label)[] ExportSourceSource = [("schedule", "Zamanlanmış"), ("manual", "Elle")];

    internal static readonly (string Key, string Label)[] ScheduleStatusSource =
        [("delivered", "Teslim edildi"), ("stopped", "Durduruldu"), ("failed", "Başarısız")];

    // "15'i", "3'ü": ayın günü (1-28) belirtme hâli eki, sayının okunuşuna göre. Ek dile özgüdür; her dilin sözlüğü kendi ekini verir (ör. "15th").
    internal static readonly (string Key, string Label)[] DaySuffixSource =
    [
        ("1", "i"), ("2", "si"), ("3", "ü"), ("4", "ü"), ("5", "i"), ("6", "sı"), ("7", "si"), ("8", "i"), ("9", "u"), ("10", "u"),
        ("11", "i"), ("12", "si"), ("13", "ü"), ("14", "ü"), ("15", "i"), ("16", "sı"), ("17", "si"), ("18", "i"), ("19", "u"), ("20", "si"),
        ("21", "i"), ("22", "si"), ("23", "ü"), ("24", "ü"), ("25", "i"), ("26", "sı"), ("27", "si"), ("28", "i"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Operators => Translate(OperatorGroup, OperatorSource);

    public static IReadOnlyList<(string Key, string Label)> Aggregations => Translate(AggregationGroup, AggregationSource);

    public static IReadOnlyList<(string Key, string Label)> Charts => Translate(ChartGroup, ChartSource);

    public static IReadOnlyList<(string Key, string Label)> Grains => Translate(GrainGroup, GrainSource);

    public static IReadOnlyList<(string Key, string Label)> Visibilities => Translate(VisibilityGroup, VisibilitySource);

    public static IReadOnlyList<(string Key, string Label)> VisibilityBadges => Translate(VisibilityBadgeGroup, VisibilityBadgeSource);

    public static IReadOnlyList<(string Key, string Label)> Directions => Translate(DirectionGroup, DirectionSource);

    public static IReadOnlyList<(string Key, string Label)> Healths => Translate(HealthGroup, HealthSource);

    public static IReadOnlyList<(string Key, string Label)> Kinds => Translate(KindGroup, KindSource);

    public static IReadOnlyList<(string Key, string Label)> Frequencies => Translate(FrequencyGroup, FrequencySource);

    public static IReadOnlyList<(string Key, string Label)> Weekdays => Translate(WeekdayGroup, WeekdaySource);

    public static IReadOnlyList<(string Key, string Label)> ExportSources => Translate(ExportSourceGroup, ExportSourceSource);

    public static IReadOnlyList<(string Key, string Label)> ScheduleStatuses => Translate(ScheduleStatusGroup, ScheduleStatusSource);

    public static IReadOnlyList<(string Key, string Label)> DaySuffixes => Translate(DaySuffixGroup, DaySuffixSource);

    public static string Operator(string key) => Lookup(OperatorGroup, OperatorSource, key);

    /// <summary>"Kayıt sayısı" ölçüsünün adı (toplama türü seçeneklerinde yer almaz).</summary>
    public static string CountText => Texts.GetOr("reports.aggregation.count", "Kayıt sayısı");

    public static string Aggregation(string key) => key == "count" ? CountText : Lookup(AggregationGroup, AggregationSource, key);

    public static string Chart(string key) => Lookup(ChartGroup, ChartSource, key);

    public static string Grain(string? key) => Lookup(GrainGroup, GrainSource, key ?? "month");

    public static string Direction(string key) => Lookup(DirectionGroup, DirectionSource, key);

    /// <summary>Paylaşım rozeti metni: özel / seçilen kullanıcılar / firma.</summary>
    public static string Visibility(string key, int sharedWithCount = 0) => key == "users" && sharedWithCount > 0
        ? string.Format(CultureInfo.InvariantCulture, Texts.GetOr("reports.visibility-badge.usersCount", "Seçilen kullanıcılar ({0})"), sharedWithCount)
        : Lookup(VisibilityBadgeGroup, VisibilityBadgeSource, key);

    public static string Health(string key) => Lookup(HealthGroup, HealthSource, key);

    /// <summary>Rozet sınıfı: renk tek başına bilgi taşımaz, metin her zaman yazılır.</summary>
    public static string HealthBadge(string key) => key == "ok" ? "status-customer" : "status-prospect";

    public static string Kind(string key) => Lookup(KindGroup, KindSource, key);

    /// <summary>Sütun türüne göre kullanılabilir süzgeç işleçleri (sunucudaki <c>ReportSpecValidator</c> kurallarıyla aynı).</summary>
    public static IReadOnlyList<string> OperatorsFor(string kind) => kind switch
    {
        "text" => ["equals", "notEquals", "contains", "in", "isEmpty", "notEmpty"],
        "number" or "money" or "date" or "dateTime" => ["equals", "notEquals", "greater", "greaterOrEqual", "less", "lessOrEqual", "isEmpty", "notEmpty"],
        "flag" => ["equals", "notEquals"],
        _ => ["equals", "notEquals", "in", "isEmpty", "notEmpty"],
    };

    public static bool NeedsValue(string op) => op is not ("isEmpty" or "notEmpty");

    public static bool IsDate(string kind) => kind is "date" or "dateTime";

    /// <summary>Ölçü kimliği: sıralama ve sonuç sütunu anahtarı ("count", "sum:amount"); sunucudaki <c>MeasureId</c> ile aynıdır.</summary>
    public static string MeasureId(ReportMeasureDto measure) =>
        measure.Aggregation == "count" && measure.Field is null ? "count" : $"{measure.Aggregation}:{measure.Field}";

    /// <summary>Hazır rapor ya da kayıtlı rapor gibi tanımın ne tür rapor olduğunu söyler.</summary>
    public static bool IsSummary(ReportSpecDto spec) => spec.Groups.Count > 0 || spec.Measures.Count > 0;

    public static string Number(decimal value, bool wholeAsInteger) =>
        value.ToString(wholeAsInteger && value == decimal.Truncate(value) ? "N0" : "N2", Regional.Numbers);

    private static string EmptyGroup => Texts.GetOr("reports.cell.empty", "(boş)");

    private static string FlagText(bool value) => value ? Texts.GetOr("common.yes", "Evet") : Texts.GetOr("common.no", "Hayır");

    /// <summary>
    /// Sonuç hücresinin görünen metni. Değer yoksa "—" (ölçüde "değer yok"); sıfır "0" olarak yazılır, böylece "veri yok" ile gerçek sıfır karışmaz.
    /// Grup sütunundaki boş anahtar "(boş)" gösterilir.
    /// </summary>
    public static string Cell(JsonElement cell, ReportResultColumnDto column, string? currency = null)
    {
        if (cell.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return column.Role == "group" ? EmptyGroup : "—";
        }

        switch (column.Kind)
        {
            case "number" or "money" when cell.ValueKind == JsonValueKind.Number && cell.TryGetDecimal(out var number):
                var text = Number(number, column.Kind == "number");
                return column.Role == "value" && column.Kind == "money" && !string.IsNullOrEmpty(currency) ? $"{text} {currency}" : text;
            case "flag":
                return cell.ValueKind switch
                {
                    JsonValueKind.True => FlagText(true),
                    JsonValueKind.False => FlagText(false),
                    JsonValueKind.String when bool.TryParse(cell.GetString(), out var flag) => FlagText(flag),
                    _ => cell.ToString(),
                };
            case "date" or "dateTime" when cell.ValueKind == JsonValueKind.String:
                return DateText(cell.GetString() ?? string.Empty);
        }

        var raw = cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.ToString();
        return column.Role == "group" && raw.Length == 0 ? EmptyGroup : raw;
    }

    /// <summary>
    /// Satırın kısa adı (düğme/bağlantı etiketi ve ayrıntı başlığı): özet satırında grup değerleri ve para birimi, kayıt satırında ilk dolu hücre.
    /// </summary>
    public static string RowTitle(ReportResultDto result, ReportResultRowDto row)
    {
        var parts = new List<string>();
        for (var i = 0; i < result.Columns.Count && i < row.Cells.Count; i++)
        {
            var column = result.Columns[i];
            if (result.IsSummary ? column.Role == "group" : row.Cells[i].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                parts.Add(Cell(row.Cells[i], column));
                if (!result.IsSummary)
                {
                    break;
                }
            }
        }

        var title = parts.Count == 0 ? Texts.GetOr("reports.allRecords", "Tüm kayıtlar") : string.Join(" / ", parts);
        return result.IsSummary && !string.IsNullOrEmpty(row.Currency) ? $"{title} ({row.Currency})" : title;
    }

    /// <summary>"yyyy-MM-dd" ya da "yyyy-MM-dd HH:mm" → firmanın tarih biçimi; gruplamadaki "yyyy-MM" ve "yyyy" olduğu gibi kalır.</summary>
    private static string DateText(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return CrmLabels.Date(day);
        }

        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return $"{CrmLabels.Date(DateOnly.FromDateTime(time))} {time.ToString(Regional.TimePattern, CultureInfo.InvariantCulture)}";
        }

        return value;
    }

    /// <summary>Hücrenin sayısal değeri (sayı ölçüsü değilse ya da boşsa null); ayrıntı toplamı karşılaştırması için.</summary>
    public static decimal? Decimal(JsonElement cell) =>
        cell.ValueKind == JsonValueKind.Number && cell.TryGetDecimal(out var value) ? value : null;

    // ---- Dışa aktarma ve zamanlama (dilim 4)

    public const int MaxSchedules = 10;

    /// <summary>Sunucudaki <c>ReportScheduleRules.MaxRecipients</c> ile aynı (sahibi dışındaki en fazla alıcı).</summary>
    public const int MaxRecipients = 20;

    public static string ExportSource(string key) => Lookup(ExportSourceGroup, ExportSourceSource, key);

    public static string Hour(int hour) => $"{hour:00}:00";

    /// <summary>Sıklık özeti: "Her gün 09:00", "Her pazartesi 07:00", "Her ayın 15'i 06:00". Saat firmanın saat dilimindeki yerel saattir.</summary>
    public static string ScheduleSummary(ReportScheduleDto schedule) => schedule.Frequency switch
    {
        "weekly" => Texts.Get("reports.schedule.weekly", Lookup(WeekdayGroup, WeekdaySource, schedule.DayOfWeek), Hour(schedule.Hour)),
        "monthly" when schedule.DayOfMonth is { } day => Texts.Get(
            "reports.schedule.monthlyDay", day, Lookup(DaySuffixGroup, DaySuffixSource, day.ToString(CultureInfo.InvariantCulture)), Hour(schedule.Hour)),
        "monthly" => Texts.Get("reports.schedule.monthly", Hour(schedule.Hour)),
        _ => Texts.Get("reports.schedule.daily", Hour(schedule.Hour)),
    };

    /// <summary>Son çalışma durumu (Delivered/Stopped/Failed; büyük/küçük harf fark etmez); tabloda olmayan durum olduğu gibi döner.</summary>
    public static string ScheduleStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return Texts.GetOr("reports.schedule-status.never", "Henüz çalışmadı");
        }

        var key = status.ToLowerInvariant();
        return ScheduleStatusSource.Any(s => s.Key == key) ? Lookup(ScheduleStatusGroup, ScheduleStatusSource, key) : status;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0.#", Regional.Numbers)} KB",
        _ => $"{(bytes / 1024d / 1024d).ToString("0.##", Regional.Numbers)} MB",
    };

    /// <summary>Dosya indirme hata kodlarının kullanıcıya söylenen karşılıkları (sunucunun iletisi varsa o da eklenir).</summary>
    public static string DownloadError(System.Net.HttpStatusCode status, string? serverMessage) => status switch
    {
        System.Net.HttpStatusCode.NotFound => Texts.Get("reports.download.notFound"),
        System.Net.HttpStatusCode.Gone => Texts.Get("reports.download.gone"),
        System.Net.HttpStatusCode.Forbidden => Texts.Get("reports.download.forbidden"),
        System.Net.HttpStatusCode.Conflict => Texts.Get("reports.download.conflict"),
        _ => serverMessage ?? Texts.Get("reports.download.failed"),
    };

    /// <summary>Kayıt bağlantısı: sütun kümesinin <c>recordPath</c>'indeki <c>{id}</c> kayıt kimliğiyle değişir.</summary>
    public static string? RecordLink(string? recordPath, Guid? id) =>
        recordPath is { Length: > 0 } && id is { } recordId ? recordPath.Replace("{id}", recordId.ToString(), StringComparison.Ordinal).TrimStart('/') : null;
}
