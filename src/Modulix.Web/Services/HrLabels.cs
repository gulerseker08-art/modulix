using Modulix.Shared.Hr;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>İK ekranlarının etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class HrLabels
{
    public static string Status(string? key) => HrAccess.Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Çalışıyor başarı tonu, izinde dikkat tonu, ayrılan nötr ton.</summary>
    public static string StatusBadge(string? key) => key switch
    {
        "active" => "status-customer",
        "onLeave" => "stage-negotiation",
        _ => "status-inactive",
    };

    public static string EmploymentType(string? key) => HrAccess.EmploymentTypes.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>"yyyy-AA-gg" tarihini gg.AA.yyyy gösterir; boşsa "—".</summary>
    public static string Date(string? iso) => FleetLabels.Day(iso) is { } day ? FleetLabels.Date(day) : "—";

    /// <summary>Çalışanın adı soyadı (kişi kaydından gelen hesaplanan alan).</summary>
    public static string Name(RecordResponse employee) => WarehouseLabels.Text(employee, "fullName") is { Length: > 0 } name ? name : "—";

    /// <summary>Avatar baş harfleri (ad ve soyadın ilk harfleri).</summary>
    public static string Initials(RecordResponse employee)
    {
        var first = WarehouseLabels.Text(employee, "firstName");
        var last = WarehouseLabels.Text(employee, "lastName");
        return string.Concat(first is { Length: > 0 } f ? f[..1] : string.Empty, last is { Length: > 0 } l ? l[..1] : string.Empty).ToUpper(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
    }

    public static string LeaveStatus(string? key) => HrAccess.LeaveStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Bekleyen dikkat tonu, onaylanan başarı tonu, reddedilen kayıp tonu, iptal nötr ton.</summary>
    public static string LeaveStatusBadge(string? key) => key switch
    {
        "pending" => "stage-negotiation",
        "approved" => "status-customer",
        "rejected" => "stage-lost",
        _ => "status-inactive",
    };

    /// <summary>Gün sayısı: "3 gün", "0,5 gün".</summary>
    public static string Days(decimal? days) => days is { } d ? $"{d.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} gün" : "—";

    public static decimal? Decimal(RecordResponse record, string field) => record.Values[field]?.GetValue<decimal>();

    /// <summary>İzin aralığı: "12.11.2026 – 14.11.2026" (tek günde tek tarih, yarım günde "(yarım gün)").</summary>
    public static string Range(RecordResponse request)
    {
        var start = Date(WarehouseLabels.Text(request, "startDate"));
        var end = Date(WarehouseLabels.Text(request, "endDate"));
        var half = WarehouseLabels.Flag(request, "halfDay") ? " (yarım gün)" : string.Empty;
        return (start == end ? start : $"{start} – {end}") + half;
    }
}
