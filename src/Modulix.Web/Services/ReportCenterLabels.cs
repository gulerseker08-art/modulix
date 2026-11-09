using System.Globalization;
using System.Text.Json;
using Modulix.Shared.Reporting;

namespace Modulix.Web.Services;

/// <summary>
/// Rapor merkezinin (ADR-0035 dilim 3) etiket ve biçim yardımcıları. Sabit değerler sunucu sözleşmesindeki camelCase metinlerdir
/// (<c>ReportContracts.cs</c> baş yorumu); burada yalnızca Türkçe adları ve gösterim biçimleri vardır, hesap sunucudadır.
/// (Destek raporlarının yardımcıları <see cref="ReportLabels"/>'tadır.)
/// </summary>
public static class ReportCenterLabels
{
    public const string Table = "table";
    public const string Summary = "summary";

    public static IReadOnlyList<(string Key, string Label)> Operators { get; } =
    [
        ("equals", "eşittir"), ("notEquals", "eşit değildir"), ("greater", "büyüktür"), ("greaterOrEqual", "büyük veya eşittir"),
        ("less", "küçüktür"), ("lessOrEqual", "küçük veya eşittir"), ("contains", "içerir"), ("in", "şunlardan biri"),
        ("isEmpty", "boştur"), ("notEmpty", "boş değildir"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Aggregations { get; } =
        [("sum", "Toplam"), ("average", "Ortalama"), ("min", "En düşük"), ("max", "En yüksek")];

    public static IReadOnlyList<(string Key, string Label)> Charts { get; } =
        [("none", "Grafik yok"), ("bar", "Çubuk"), ("line", "Çizgi"), ("pie", "Pasta")];

    public static IReadOnlyList<(string Key, string Label)> Grains { get; } = [("day", "Gün"), ("month", "Ay"), ("year", "Yıl")];

    public static IReadOnlyList<(string Key, string Label)> Visibilities { get; } =
    [
        ("private", "Yalnızca ben"), ("users", "Seçtiğim kullanıcılar"), ("tenant", "Firmadaki herkes"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Directions { get; } = [("ascending", "Artan"), ("descending", "Azalan")];

    private static string Find(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(i => i.Key == key).Label ?? key ?? string.Empty;

    public static string Operator(string key) => Find(Operators, key);

    public static string Aggregation(string key) => key == "count" ? "Kayıt sayısı" : Find(Aggregations, key);

    public static string Chart(string key) => Find(Charts, key);

    public static string Grain(string? key) => Find(Grains, key ?? "month");

    public static string Direction(string key) => Find(Directions, key);

    /// <summary>Paylaşım rozeti metni: özel / seçilen kullanıcılar / firma.</summary>
    public static string Visibility(string key, int sharedWithCount = 0) => key switch
    {
        "private" => "Özel",
        "users" => sharedWithCount > 0 ? $"Seçilen kullanıcılar ({sharedWithCount})" : "Seçilen kullanıcılar",
        "tenant" => "Firma",
        _ => key,
    };

    public static string Health(string key) => key switch
    {
        "ok" => "Çalışır",
        "datasetMissing" => "Veri kümesi yok",
        "moduleDisabled" => "Modül etkin değil",
        "forbidden" => "Yetkiniz yok",
        "columnsInvalid" => "Sütun geçersiz",
        _ => key,
    };

    /// <summary>Rozet sınıfı: renk tek başına bilgi taşımaz, metin her zaman yazılır.</summary>
    public static string HealthBadge(string key) => key == "ok" ? "status-customer" : "status-prospect";

    public static string Kind(string key) => key switch
    {
        "text" => "Metin",
        "number" => "Sayı",
        "money" => "Tutar",
        "date" => "Tarih",
        "dateTime" => "Tarih ve saat",
        "flag" => "Evet/Hayır",
        "choice" => "Seçim",
        "lookup" => "Kayıt",
        "user" => "Kullanıcı",
        _ => key,
    };

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

    /// <summary>
    /// Sonuç hücresinin görünen metni. Değer yoksa "—" (ölçüde "değer yok"); sıfır "0" olarak yazılır, böylece "veri yok" ile gerçek sıfır karışmaz.
    /// Grup sütunundaki boş anahtar "(boş)" gösterilir.
    /// </summary>
    public static string Cell(JsonElement cell, ReportResultColumnDto column, string? currency = null)
    {
        if (cell.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return column.Role == "group" ? "(boş)" : "—";
        }

        switch (column.Kind)
        {
            case "number" or "money" when cell.ValueKind == JsonValueKind.Number && cell.TryGetDecimal(out var number):
                var text = Number(number, column.Kind == "number");
                return column.Role == "value" && column.Kind == "money" && !string.IsNullOrEmpty(currency) ? $"{text} {currency}" : text;
            case "flag":
                return cell.ValueKind switch
                {
                    JsonValueKind.True => "Evet",
                    JsonValueKind.False => "Hayır",
                    JsonValueKind.String when bool.TryParse(cell.GetString(), out var flag) => flag ? "Evet" : "Hayır",
                    _ => cell.ToString(),
                };
            case "date" or "dateTime" when cell.ValueKind == JsonValueKind.String:
                return DateText(cell.GetString() ?? string.Empty);
        }

        var raw = cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.ToString();
        return column.Role == "group" && raw.Length == 0 ? "(boş)" : raw;
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

        var title = parts.Count == 0 ? "Tüm kayıtlar" : string.Join(" / ", parts);
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

    public static IReadOnlyList<(string Key, string Label)> Frequencies { get; } = [("daily", "Her gün"), ("weekly", "Her hafta"), ("monthly", "Her ay")];

    /// <summary>Haftanın günleri Pazartesi'den başlar (Türkiye); anahtarlar sözleşmedeki sunday..saturday'dir.</summary>
    public static IReadOnlyList<(string Key, string Label)> Weekdays { get; } =
    [
        ("monday", "pazartesi"), ("tuesday", "salı"), ("wednesday", "çarşamba"), ("thursday", "perşembe"),
        ("friday", "cuma"), ("saturday", "cumartesi"), ("sunday", "pazar"),
    ];

    public const int MaxSchedules = 10;

    /// <summary>Sunucudaki <c>ReportScheduleRules.MaxRecipients</c> ile aynı (sahibi dışındaki en fazla alıcı).</summary>
    public const int MaxRecipients = 20;

    public static string ExportSource(string key) => key switch { "schedule" => "Zamanlanmış", "manual" => "Elle", _ => key };

    public static string Hour(int hour) => $"{hour:00}:00";

    // "15'i", "3'ü": ayın günü (1-28) belirtme hâli eki, sayının okunuşuna göre.
    private static readonly Dictionary<int, string> DaySuffix = new()
    {
        [1] = "i", [2] = "si", [3] = "ü", [4] = "ü", [5] = "i", [6] = "sı", [7] = "si", [8] = "i", [9] = "u", [10] = "u",
        [11] = "i", [12] = "si", [13] = "ü", [14] = "ü", [15] = "i", [16] = "sı", [17] = "si", [18] = "i", [19] = "u", [20] = "si",
        [21] = "i", [22] = "si", [23] = "ü", [24] = "ü", [25] = "i", [26] = "sı", [27] = "si", [28] = "i",
    };

    /// <summary>Sıklık özeti: "Her gün 09:00", "Her pazartesi 07:00", "Her ayın 15'i 06:00". Saat firmanın saat dilimindeki yerel saattir.</summary>
    public static string ScheduleSummary(ReportScheduleDto schedule) => schedule.Frequency switch
    {
        "weekly" => $"Her {Find(Weekdays, schedule.DayOfWeek)} {Hour(schedule.Hour)}",
        "monthly" when schedule.DayOfMonth is { } day => $"Her ayın {day}'{DaySuffix.GetValueOrDefault(day, "i")} {Hour(schedule.Hour)}",
        "monthly" => $"Her ay {Hour(schedule.Hour)}",
        _ => $"Her gün {Hour(schedule.Hour)}",
    };

    /// <summary>Son çalışma durumu (Delivered/Stopped/Failed; büyük/küçük harf fark etmez).</summary>
    public static string ScheduleStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "delivered" => "Teslim edildi",
        "stopped" => "Durduruldu",
        "failed" => "Başarısız",
        null or "" => "Henüz çalışmadı",
        _ => status,
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024d).ToString("0.#", Regional.Numbers)} KB",
        _ => $"{(bytes / 1024d / 1024d).ToString("0.##", Regional.Numbers)} MB",
    };

    /// <summary>Dosya indirme hata kodlarının kullanıcıya söylenen karşılıkları (sunucunun iletisi varsa o da eklenir).</summary>
    public static string DownloadError(System.Net.HttpStatusCode status, string? serverMessage) => status switch
    {
        System.Net.HttpStatusCode.NotFound => "Dosya bulunamadı; silinmiş olabilir.",
        System.Net.HttpStatusCode.Gone => "Dosyanın süresi doldu (dosyalar 24 saat saklanır). Raporu yeniden dışa aktarın.",
        System.Net.HttpStatusCode.Forbidden => "Dışa aktarma yetkiniz yok ya da raporun modülü bu firmada kapalı; dosya indirilemez.",
        System.Net.HttpStatusCode.Conflict => "Yetkiniz dosya hazırlandıktan sonra değişti; güvenlik için dosya verilmiyor. Raporu yeniden dışa aktarın.",
        _ => serverMessage ?? "Dosya indirilemedi.",
    };

    /// <summary>Kayıt bağlantısı: sütun kümesinin <c>recordPath</c>'indeki <c>{id}</c> kayıt kimliğiyle değişir.</summary>
    public static string? RecordLink(string? recordPath, Guid? id) =>
        recordPath is { Length: > 0 } && id is { } recordId ? recordPath.Replace("{id}", recordId.ToString(), StringComparison.Ordinal).TrimStart('/') : null;
}
