using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>Sipariş ekranlarının etiketleri; anahtarlar sunucudaki <c>OrderStatus</c>, <c>HandoverStatus</c> ve <c>FulfillmentStatus</c> ile aynıdır.</summary>
public static class OrderLabels
{
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("Draft", "Taslak"), ("PendingApproval", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Cancelled", "İptal edildi"), ("Closed", "Kapatıldı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Handovers { get; } =
    [
        ("NotStarted", "Devir başlamadı"), ("Preparing", "Devir hazırlanıyor"), ("Ready", "Devre hazır"), ("Sent", "Operasyona gönderildi"),
        ("InfoRequested", "Bilgi bekleniyor"), ("Accepted", "Operasyon kabul etti"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Fulfillments { get; } =
    [
        ("NotStarted", "Başlamadı"), ("Planned", "Planlandı"), ("Partial", "Kısmen gerçekleşti"), ("Completed", "Tamamlandı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> LineKinds { get; } =
        [("Product", "Ürün"), ("Service", "Hizmet"), ("Package", "Paket"), ("Free", "Serbest kalem")];

    public static IReadOnlyList<(string Key, string Label)> AcceptanceMethods { get; } =
        [("Portal", "Müşteri sayfasından"), ("Manual", "Satış kullanıcısı kaydetti")];

    public static IReadOnlyList<(string Key, string Label)> Sorts { get; } =
    [
        ("-createdAt", "En yeni"), ("createdAt", "En eski"), ("number", "Numara (artan)"), ("-number", "Numara (azalan)"),
        ("deliveryDate", "Teslim tarihi (yakın)"), ("-deliveryDate", "Teslim tarihi (uzak)"), ("title", "Başlık (A-Z)"),
    ];

    public static IReadOnlyList<(string Key, string Label)> JobStatuses { get; } =
    [
        ("AwaitingAssignment", "Atama bekliyor"), ("Planned", "Planlandı"), ("InProgress", "Devam ediyor"), ("Completed", "Tamamlandı"), ("Cancelled", "İptal edildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> JobKinds { get; } =
        [("Delivery", "Teslimat"), ("Service", "Hizmet başlangıcı"), ("Mixed", "Karma"), ("Other", "Diğer")];

    public static IReadOnlyList<(string Key, string Label)> Priorities { get; } =
        [("Low", "Düşük"), ("Normal", "Normal"), ("High", "Yüksek"), ("Urgent", "Acil")];

    public static IReadOnlyList<(string Key, string Label)> JobSorts { get; } =
    [
        ("-createdAt", "En yeni"), ("plannedEnd", "Planlanan bitiş (yakın)"), ("-plannedEnd", "Planlanan bitiş (uzak)"), ("number", "Numara (artan)"), ("title", "Başlık (A-Z)"),
    ];

    public static string JobBadge(string status) => status switch
    {
        "Completed" => "won",
        "InProgress" => "onhold",
        "AwaitingAssignment" => "lost",
        "Cancelled" => "cancelled",
        _ => "open",
    };

    public static string JobAction(string key) => key switch
    {
        "assign" => "Ata",
        "start" => "Başlat",
        "complete" => "Tamamla",
        "cancel" => "İptal et",
        _ => key,
    };

    public static string JobHistory(string kind) => kind switch
    {
        "Generated" => "Devir kabulünden üretildi",
        "CreatedManually" => "Elle açıldı",
        "Assigned" => "Atama yapıldı",
        "Started" => "Başlatıldı",
        "Completed" => "Tamamlandı",
        "Cancelled" => "İptal edildi",
        "DependencyAdded" => "Bağımlılık eklendi",
        "DependencyRemoved" => "Bağımlılık kaldırıldı",
        "UnassignedAlert" => "Atama bekleme takibi açıldı",
        "OverdueAlert" => "Gecikme takibi açıldı",
        _ => kind,
    };

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string StatusBadge(string status) => status switch
    {
        "Approved" => "won",
        "PendingApproval" => "onhold",
        "Cancelled" => "cancelled",
        "Closed" => "lost",
        _ => "open",
    };

    public static string HandoverBadge(string status) => status switch
    {
        "Accepted" => "won",
        "Sent" or "Ready" => "onhold",
        "InfoRequested" => "lost",
        _ => "open",
    };

    public static string FulfillmentBadge(string status) => status switch
    {
        "Completed" => "won",
        "Partial" or "Planned" => "onhold",
        _ => "open",
    };

    public static IReadOnlyList<(string Key, string Label)> ChangeStatuses { get; } =
        [("Pending", "Karar bekliyor"), ("Approved", "Onaylandı ve uygulandı"), ("Rejected", "Reddedildi"), ("Withdrawn", "Geri çekildi")];

    public static string ChangeBadge(string status) => status switch
    {
        "Approved" => "won",
        "Pending" => "onhold",
        "Rejected" => "lost",
        _ => "cancelled",
    };

    public static IReadOnlyList<(string Key, string Label)> HeaderFields { get; } =
    [
        ("deliveryAddress", "Teslimat adresi"), ("location", "Lokasyon"), ("plannedStartDate", "Planlanan başlangıç"), ("plannedDeliveryDate", "Planlanan teslim"),
        ("paymentTerms", "Ödeme koşulları"), ("deliveryTerms", "Teslimat koşulları"), ("commercialTerms", "Ticari koşullar"),
        ("commitments", "Müşteriye verilen taahhütler"), ("operationsNotes", "Operasyon notları"),
    ];

    public static string Action(string key) => key switch
    {
        "submit-for-approval" => "Onaya gönder",
        "withdraw-approval" => "Onayı geri çek",
        "approve" => "Onayla",
        "return-to-draft" => "Taslağa geri gönder",
        "cancel" => "Siparişi iptal et",
        "close" => "Siparişi kapat",
        "force-close" => "Zorla kapat",
        _ => key,
    };

    public static bool IsPrimary(string key) => key is "submit-for-approval" or "approve" or "close";

    public static bool IsDanger(string key) => key is "cancel" or "force-close";

    public static string History(string kind) => kind switch
    {
        "CreatedFromQuote" => "Kabul edilmiş tekliften oluşturuldu",
        "SubmittedForApproval" => "Onaya gönderildi",
        "ApprovalWithdrawn" => "Onay geri çekildi",
        "Approved" => "Onaylandı",
        "ReturnedToDraft" => "Onaydan taslağa geri gönderildi",
        "Cancelled" => "İptal edildi",
        "Closed" => "Kapatıldı",
        "ForceClosed" => "Kalan yükümlülükle zorla kapatıldı",
        "ChangeRequested" => "Değişiklik talebi açıldı",
        "ChangeApplied" => "Değişiklik talebi onaylandı ve uygulandı",
        "ChangeRejected" => "Değişiklik talebi reddedildi",
        "ChangeWithdrawn" => "Değişiklik talebi geri çekildi",
        "DeliveryPlanned" => "Teslimat planlandı",
        "Delivered" => "Teslimat kaydedildi",
        "DeliveryFailed" => "Teslimat başarısız",
        "DeliveryRescheduled" => "Teslimat yeniden planlandı",
        "DeliveryCancelled" => "Teslimat iptal edildi",
        "EvidenceAdded" => "Teslim kanıtı eklendi",
        "ServicePlanned" => "Hizmet başlangıcı planlandı",
        "ServiceStarted" => "Hizmet başladı",
        "ServiceConfirmed" => "Müşteri başlangıcı onayladı",
        "ServiceCompleted" => "Hizmet tamamlandı",
        "FulfillmentChanged" => "Gerçekleşme durumu değişti",
        _ => kind,
    };

    /// <summary>Tutar; izin yoksa (null) gizli olduğu söylenir. Para birimleri karıştırılmaz: her tutar kendi para biriminde gösterilir.</summary>
    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{value.ToString("N2", Regional.Numbers)} {currency}" : "Gizli";

    public static string Quantity(decimal value, string? unit) => $"{value.ToString("G29", Regional.Numbers)} {unit}".TrimEnd();

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, CultureInfo.InvariantCulture) : "—";
}
