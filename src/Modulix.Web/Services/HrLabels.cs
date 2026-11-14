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
    public const string Everyone = "Herkes";

    /// <summary>Kayıttaki kimlik listesi alanı (ör. hedef departmanlar); boşsa boş liste.</summary>
    public static IReadOnlyList<Guid> Ids(RecordResponse record, string field) =>
        record.Values[field] is System.Text.Json.Nodes.JsonArray array
            ? [.. array.Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]
            : [];

    /// <summary>Kimlik listesinin JSON'u (kayıt yazarken).</summary>
    public static System.Text.Json.Nodes.JsonArray IdArray(IEnumerable<Guid> ids) =>
        [.. ids.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id.ToString()))];

    /// <summary>
    /// Duyuru/anket hedefi: Core departmanının ağaç yolu (bölüm 22), Core'a eşlenmemiş eski kayıtta eski İK departmanının adı, hedef yoksa "Herkes".
    /// </summary>
    public static string Audience(RecordResponse record, CrmLookups lookups) =>
        WarehouseLabels.Text(record, "audienceName") is { Length: > 0 } unit ? unit
        : WarehouseLabels.Text(record, "audienceDepartmentKey") is { Length: > 0 } legacy ? lookups.Label(HrAccess.DepartmentList, legacy)
        : Everyone;

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

    public static string ExpenseStatus(string? key) => HrAccess.ExpenseStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Taslak nötr, onay bekleyen dikkat, onaylanan marka, ödenen başarı, reddedilen kayıp, iptal nötr ton.</summary>
    public static string ExpenseStatusBadge(string? key) => key switch
    {
        "submitted" => "stage-negotiation",
        "approved" => "stage-qualified",
        "paid" => "status-customer",
        "rejected" => "stage-lost",
        _ => "status-inactive",
    };

    /// <summary>Tutar ve para birimi: "1.250,50 TRY".</summary>
    public static string Money(decimal? amount, string? currency) =>
        amount is { } value ? $"{value.ToString("#,##0.00", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} {currency}".Trim() : "—";

    public static string AssetStatus(string? key) => HrAccess.AssetStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Depoda başarı, zimmetli marka, bakımda dikkat, kayıp kayıp tonu, hurda nötr.</summary>
    public static string AssetStatusBadge(string? key) => key switch
    {
        "inStock" => "status-customer",
        "assigned" => "stage-qualified",
        "maintenance" => "stage-negotiation",
        "lost" => "stage-lost",
        _ => "status-inactive",
    };

    public static string AssignmentStatus(string? key) => HrAccess.AssignmentStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string AssignmentStatusBadge(string? key) => key switch
    {
        "active" => "stage-qualified",
        "returned" => "status-customer",
        "lost" => "stage-lost",
        _ => "status-inactive",
    };

    public static string ChecklistKind(string? key) => HrAccess.ChecklistKinds.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string ChecklistStatus(string? key) => HrAccess.ChecklistStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string ChecklistStatusBadge(string? key) => key switch
    {
        "open" => "stage-qualified",
        "completed" => "status-customer",
        _ => "status-inactive",
    };

    public static string TaskStatus(string? key) => HrAccess.TaskStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string TaskStatusBadge(string? key) => key switch
    {
        "open" => "stage-negotiation",
        "done" => "status-customer",
        _ => "status-inactive",
    };

    public static string Responsible(string? key) => HrAccess.ChecklistResponsibles.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string TaskType(string? key) => HrAccess.ChecklistTaskTypes.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string SurveyStatus(string? key) => HrAccess.SurveyStatuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    public static string SurveyStatusBadge(string? key) => key switch
    {
        "open" => "stage-qualified",
        "draft" => "stage-negotiation",
        _ => "status-inactive",
    };

    public static string QuestionType(string? key) => HrAccess.QuestionTypes.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;
}
