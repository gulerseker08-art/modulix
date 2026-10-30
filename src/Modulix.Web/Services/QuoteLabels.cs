namespace Modulix.Web.Services;

/// <summary>Teklif durumlarının ve eylemlerinin Türkçe etiketleri (ADR-0019); anahtarlar sunucudaki QuoteStatus/QuoteAction ile aynıdır.</summary>
public static class QuoteLabels
{
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("Draft", "Taslak"), ("Presented", "Müşteriye sunuldu"), ("Accepted", "Kabul edildi"), ("Rejected", "Reddedildi"),
        ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"), ("Superseded", "Yeni revizyonla geçersiz"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Approvals { get; } =
    [
        ("NotRequired", "Onay gerekmiyor"), ("Pending", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Rejected", "Onay reddedildi"),
        ("ChangesRequested", "Değişiklik istendi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Deliveries { get; } =
    [
        ("NotSent", "Gönderilmedi"), ("Pending", "Gönderim bekliyor"), ("Processing", "İşleniyor"),
        ("Succeeded", "Sağlayıcı kabul etti"), ("Failed", "Gönderilemedi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> LineKinds { get; } =
        [("Product", "Ürün"), ("Service", "Hizmet"), ("Package", "Paket"), ("Free", "Serbest kalem")];

    public static IReadOnlyList<(string Key, string Label)> Roles { get; } =
    [
        ("Standard", "Standart"), ("Optional", "Opsiyonel"), ("Alternative", "Alternatif"), ("PackageChild", "Paket bileşeni"),
    ];

    public static IReadOnlyList<(string Key, string Label)> DiscountKinds { get; } =
        [("None", "İndirim yok"), ("Percent", "Yüzde (%)"), ("Amount", "Tutar")];

    public static IReadOnlyList<(string Key, string Label)> Periods { get; } =
        [("", "Tek seferlik"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    /// <summary>Mevcut rozet sınıfları; durum her zaman metinle birlikte gösterilir (yalnızca renkle ifade edilmez).</summary>
    public static string StatusBadge(string? status) => status switch
    {
        "Presented" => "qualified",
        "Accepted" => "won",
        "Rejected" => "lost",
        "Expired" or "Cancelled" => "cancelled",
        "Superseded" => "onhold",
        _ => "open",
    };

    public static string ApprovalBadge(string? status) => status switch
    {
        "Approved" => "won",
        "Rejected" => "lost",
        "Pending" => "qualified",
        "ChangesRequested" => "onhold",
        _ => "open",
    };

    public static string Action(string action) => action switch
    {
        "submit-for-approval" => "Onaya gönder",
        "withdraw-approval" => "Onayı geri çek",
        "approve" => "Onayla",
        "reject-approval" => "Onayı reddet",
        "request-changes" => "Değişiklik iste",
        "present" => "Müşteriye sun",
        "accept" => "Kabul edildi olarak kaydet",
        "reject" => "Reddedildi olarak kaydet",
        "cancel" => "İptal et",
        _ => action,
    };

    /// <summary>Uç noktadaki yol parçası (sunucu eylem anahtarından farklı olanlar).</summary>
    public static string Route(string action) => action switch
    {
        "accept" => "record-accepted",
        "reject" => "record-rejected",
        _ => action,
    };

    public static bool NeedsReason(string action) => action is "reject-approval" or "request-changes" or "reject" or "cancel";

    public static string EventLabel(string kind) => kind switch
    {
        "Created" => "Oluşturuldu",
        "Revised" => "Revizyon açıldı",
        "SubmittedForApproval" => "Onaya gönderildi",
        "ApprovalWithdrawn" => "Onay geri çekildi",
        "Approved" => "Onaylandı",
        "ApprovalRejected" => "Onay reddedildi",
        "ChangesRequested" => "Değişiklik istendi",
        "ApprovalInvalidated" => "Onay geçersizleşti",
        "Presented" => "Müşteriye sunuldu",
        "Accepted" => "Kabul kaydedildi",
        "Rejected" => "Ret kaydedildi",
        "Cancelled" => "İptal edildi",
        "Superseded" => "Yeni revizyonla geçersizleşti",
        "DocumentGenerated" => "Müşteri belgesi (PDF) sabitlendi",
        "DeliveryUpdated" => "Gönderim durumu güncellendi",
        "Expired" => "Geçerlilik süresi doldu",
        "OpportunityWon" => "Bağlı fırsat kazanıldı",
        "OpportunityWinSkipped" => "Bağlı fırsat kazanıldı yapılmadı",
        "FollowUpScheduled" => "Takip aktivitesi açıldı",
        "FollowUpsClosed" => "Takip aktiviteleri otomatik kapatıldı",
        "ExpiryReminderCreated" => "Süre dolumu hatırlatması açıldı",
        "ShareLinkCreated" => "Paylaşım bağlantısı oluşturuldu",
        "ShareLinkRevoked" => "Paylaşım bağlantısı iptal edildi",
        "CustomerViewed" => "Müşteri ilk kez görüntüledi",
        "CustomerAccepted" => "Müşteri kabul etti",
        "CustomerRejected" => "Müşteri reddetti",
        _ => kind,
    };

    public static string Money(decimal amount, string currency) => CrmLabels.Amount(amount, currency);
}
