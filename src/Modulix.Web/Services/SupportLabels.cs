namespace Modulix.Web.Services;

/// <summary>Destek ekranlarının etiketleri; anahtarlar sunucudaki <c>TicketStatus</c>, <c>TicketPriority</c> ve eylem adlarıyla aynıdır.</summary>
public static class SupportLabels
{
    public const string TicketTypeList = "support.ticket-type";
    public const string ResolutionCodeList = "support.resolution-code";
    public const string ComplaintCategory = "complaint";

    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("New", "Yeni"), ("Triage", "Sınıflandırmada"), ("Assigned", "Atandı"), ("InProgress", "İşlemde"), ("WaitingCustomer", "Müşteriden bilgi bekleniyor"),
        ("WaitingInternal", "İç ekip bekleniyor"), ("Resolved", "Çözüldü"), ("Closed", "Kapandı"), ("Reopened", "Yeniden açıldı"), ("Cancelled", "İptal edildi"), ("Merged", "Birleştirildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Priorities { get; } =
        [("Low", "Düşük"), ("Normal", "Normal"), ("High", "Yüksek"), ("Critical", "Kritik")];

    public static IReadOnlyList<(string Key, string Label)> Levels { get; } = [("Low", "Düşük"), ("Medium", "Orta"), ("High", "Yüksek")];

    public static IReadOnlyList<(string Key, string Label)> Channels { get; } =
        [("User", "Kullanıcı girişi"), ("Phone", "Telefon"), ("Email", "E-posta"), ("Web", "Web formu"), ("Portal", "Müşteri portalı"), ("Api", "API")];

    public static IReadOnlyList<(string Key, string Label)> Sorts { get; } =
    [
        ("-createdAt", "En yeni"), ("createdAt", "En eski"), ("priority", "Öncelik (yüksek önce)"), ("-lastActivity", "Son hareket"), ("resolutionDue", "Çözüm hedefi (yakın)"), ("firstResponseDue", "İlk yanıt hedefi (yakın)"),
        ("number", "Numara (artan)"), ("-number", "Numara (azalan)"), ("subject", "Konu (A-Z)"),
    ];

    public static IReadOnlyList<(string Key, string Label)> MessageKinds { get; } =
        [("reply", "Müşteriye yanıt"), ("note", "İç not (müşteriye gitmez)"), ("customer-message", "Müşteriden gelen mesajı kaydet"), ("customer-confirmation", "Müşteri çözümü onayladı (talebi kapatır)")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string StatusBadge(string status) => status switch
    {
        "Resolved" => "won",
        "Closed" => "lost",
        "Cancelled" or "Merged" => "cancelled",
        "WaitingCustomer" or "WaitingInternal" or "InProgress" => "onhold",
        "Reopened" => "lost",
        _ => "open",
    };

    public static string PriorityBadge(string priority) => priority switch
    {
        "Critical" => "lost",
        "High" => "onhold",
        "Low" => "cancelled",
        _ => "open",
    };

    public static string Action(string key) => key switch
    {
        "triage" => "Sınıflandırmaya al",
        "assign" => "Ata",
        "take" => "Üzerime al",
        "transfer" => "Başka kuyruğa devret",
        "start" => "İşleme al",
        "wait-customer" => "Müşteriden bilgi bekle",
        "wait-internal" => "İç ekibi bekle",
        "resume" => "Sürdür",
        "resolve" => "Çöz",
        "close" => "Kapat",
        "reopen" => "Yeniden aç",
        "cancel" => "İptal et",
        "set-priority" => "Önceliği değiştir",
        _ => key,
    };

    public static bool IsPrimary(string key) => key is "take" or "start" or "resolve" or "close";

    public static bool IsDanger(string key) => key is "cancel";

    public static string History(string kind) => kind switch
    {
        "Created" => "Talep açıldı",
        "Triaged" => "Sınıflandırmaya alındı",
        "Assigned" => "Atama yapıldı",
        "Taken" => "Üzerine alındı",
        "Transferred" => "Başka kuyruğa devredildi",
        "Started" => "İşleme alındı",
        "WaitingCustomer" => "Müşteriden bilgi beklenmeye başlandı",
        "WaitingInternal" => "İç ekip beklenmeye başlandı",
        "Resumed" => "Sürdürüldü",
        "Resolved" => "Çözüldü",
        "Closed" => "Kapatıldı",
        "Reopened" => "Yeniden açıldı",
        "Cancelled" => "İptal edildi",
        "PriorityChanged" => "Öncelik elle değiştirildi",
        "CustomerLinked" => "Müşteri/kişi bağlantısı değişti",
        "PublicReply" => "Müşteriye yanıt yazıldı",
        "CustomerMessage" => "Müşteri mesajı kaydedildi",
        "InternalNote" => "İç not eklendi",
        "RoutedByRule" => "Yönlendirme kuralıyla yönlendirildi",
        "MergedInto" => "Başka talebe birleştirildi",
        "MergeReceived" => "Birleştirilen talep alındı",
        "TaskAdded" => "Alt iş eklendi",
        "TaskCompleted" => "Alt iş tamamlandı",
        "TaskReopened" => "Alt iş yeniden açıldı",
        "CustomerConfirmed" => "Müşteri çözümü onayladı",
        "AutoClosed" => "Zaman aşımıyla otomatik kapatıldı",
        "AttachmentAdded" => "Ek eklendi",
        "InternalAttachmentAdded" => "İç nota ek eklendi",
        "SurveyAnswered" => "Memnuniyet anketi yanıtlandı",
        "LowSatisfaction" => "Düşük memnuniyet puanı alındı (takip gerekir)",
        _ => kind,
    };

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static string Delivery(string status) => status switch
    {
        "Queued" => "E-posta kuyruğuna alındı (müşteriye ulaştığı doğrulanmadı)",
        "Sent" => "Gönderildi",
        "Failed" => "Gönderilemedi",
        "Recorded" => "Kaydedildi (e-posta gönderilmedi)",
        _ => string.Empty,
    };
}
