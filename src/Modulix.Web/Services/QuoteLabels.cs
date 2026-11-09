namespace Modulix.Web.Services;

/// <summary>Teklif durumlarının ve eylemlerinin Türkçe etiketleri (ADR-0019); anahtarlar sunucudaki QuoteStatus/QuoteAction ile aynıdır.</summary>
public static class QuoteLabels
{
    private static readonly (string Key, string Label)[] StatusSource =
    [
        ("Draft", "Taslak"), ("Presented", "Müşteriye sunuldu"), ("Accepted", "Kabul edildi"), ("Rejected", "Reddedildi"),
        ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"), ("Superseded", "Yeni revizyonla geçersiz"),
    ];

    private static readonly (string Key, string Label)[] ApprovalSource =
    [
        ("NotRequired", "Onay gerekmiyor"), ("Pending", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Rejected", "Onay reddedildi"),
        ("ChangesRequested", "Değişiklik istendi"),
    ];

    private static readonly (string Key, string Label)[] DeliverySource =
    [
        ("NotSent", "Gönderilmedi"), ("Pending", "Gönderim bekliyor"), ("Processing", "İşleniyor"),
        ("Succeeded", "Sağlayıcı kabul etti"), ("Failed", "Gönderilemedi"),
    ];

    private static readonly (string Key, string Label)[] LineKindSource =
        [("Product", "Ürün"), ("Service", "Hizmet"), ("Package", "Paket"), ("Free", "Serbest kalem")];

    private static readonly (string Key, string Label)[] RoleSource =
    [
        ("Standard", "Standart"), ("Optional", "Opsiyonel"), ("Alternative", "Alternatif"), ("PackageChild", "Paket bileşeni"),
    ];

    private static readonly (string Key, string Label)[] DiscountKindSource =
        [("None", "İndirim yok"), ("Percent", "Yüzde (%)"), ("Amount", "Tutar")];

    private static readonly (string Key, string Label)[] PeriodSource =
        [("", "Tek seferlik"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public const string StatusGroup = "crm.quote-status";
    public const string ApprovalGroup = "crm.quote-approval";
    public const string DeliveryGroup = "crm.quote-delivery";
    public const string LineKindGroup = "crm.quote-linekind";
    public const string RoleGroup = "crm.quote-role";
    public const string DiscountKindGroup = "crm.quote-discount";
    public const string PeriodGroup = "crm.quote-period";
    public const string ActionGroup = "crm.quote-action";
    public const string EventGroup = "crm.quote-event";

    // Aşağıdaki tablolarda Türkçe ad kaynak metindir (ADR-0034); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
    // Özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir).
    public static IReadOnlyList<(string Key, string Label)> Statuses => Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> Approvals => Translate(ApprovalGroup, ApprovalSource);

    public static IReadOnlyList<(string Key, string Label)> Deliveries => Translate(DeliveryGroup, DeliverySource);

    public static IReadOnlyList<(string Key, string Label)> LineKinds => Translate(LineKindGroup, LineKindSource);

    public static IReadOnlyList<(string Key, string Label)> Roles => Translate(RoleGroup, RoleSource);

    public static IReadOnlyList<(string Key, string Label)> DiscountKinds => Translate(DiscountKindGroup, DiscountKindSource);

    public static IReadOnlyList<(string Key, string Label)> Periods => Translate(PeriodGroup, PeriodSource);

    private static IReadOnlyList<(string Key, string Label)> Translate(string group, (string Key, string Label)[] source) =>
        [.. source.Select(x => (x.Key, CrmText.Of(group, x.Key.Length == 0 ? "onetime" : x.Key, x.Label)))];

    /// <summary>Bekleme süresinin okunur hali ("1 saatten az", "5 saat", "3 gün"); onay listelerinde ortak.</summary>
    public static string Waiting(TimeSpan span) =>
        span.TotalHours < 1 ? Texts.Get("crm.approval.waitLessThanHour")
        : span.TotalDays < 1 ? Texts.Get("crm.approval.waitHours", (int)span.TotalHours)
        : Texts.Get("crm.approval.waitDays", (int)span.TotalDays);

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

    public static string Action(string action) => CrmText.Of(ActionGroup, action, ActionSource(action));

    private static string ActionSource(string action) => action switch
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

    public static string EventLabel(string kind) => CrmText.Of(EventGroup, kind, EventSource(kind));

    private static string EventSource(string kind) => kind switch
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
        "OpportunitySynced" => "Fırsat esas teklif/tutar eşitlemesi",
        "StepApproved" => "Onay adımı tamamlandı",
        "ApprovalReminderSent" => "Onay hatırlatması gönderildi",
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
