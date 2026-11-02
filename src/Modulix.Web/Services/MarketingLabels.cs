namespace Modulix.Web.Services;

/// <summary>Pazarlama ekranlarının Türkçe etiketleri (segment hedefi, türü, durumu, koşul operatörleri).</summary>
public static class MarketingLabels
{
    public static readonly (string Key, string Label)[] Targets = [("Company", "Firma"), ("Person", "Kişi"), ("Lead", "Lead")];

    public static readonly (string Key, string Label)[] Kinds = [("Dynamic", "Dinamik (koşuldan)"), ("Static", "Statik (elle seçilen)")];

    public static readonly (string Key, string Label)[] Statuses = [("Draft", "Taslak"), ("Active", "Etkin"), ("Archived", "Arşiv")];

    private static readonly Dictionary<string, string> Operators = new()
    {
        ["eq"] = "eşittir",
        ["ne"] = "eşit değildir (boş dahil)",
        ["contains"] = "içerir",
        ["in"] = "listedekilerden biri",
        ["between"] = "arasında",
        ["empty"] = "boş",
        ["notempty"] = "dolu",
        ["lastdays"] = "son N gün içinde",
        ["nextdays"] = "önümüzdeki N gün içinde",
        ["before"] = "öncesinde",
        ["after"] = "sonrasında",
        ["exists"] = "var",
        ["notexists"] = "yok",
    };

    public static string Label((string Key, string Label)[] list, string key) => list.FirstOrDefault(i => i.Key == key).Label ?? key;

    public static string Operator(string key) => Operators.GetValueOrDefault(key, key);

    public static string StatusBadge(string status) => status switch { "Active" => "won", "Archived" => "cancelled", _ => "lead" };

    /// <summary>Operatörün istediği değer girişi sayısı: (en az, en çok). Sunucudaki <c>SegmentOperators.ValueCount</c> ile aynıdır.</summary>
    public static (int Min, int Max) ValueCount(string op) => op switch
    {
        "empty" or "notempty" => (0, 0),
        "between" => (2, 2),
        "in" => (1, 50),
        "exists" or "notexists" => (0, 1),
        _ => (1, 1),
    };

    public static readonly (string Key, string Label)[] Channels = [("Email", "E-posta"), ("Phone", "Telefon"), ("Sms", "SMS"), ("WhatsApp", "WhatsApp")];

    public static readonly (string Key, string Label)[] CampaignStatuses = [("Draft", "Taslak"), ("Archived", "Arşiv")];

    public static readonly (string Key, string Label)[] RecipientRules =
    [
        ("PrimaryContact", "Birincil irtibat kişisi"), ("DecisionMaker", "Karar verici kişiler"), ("Role", "Belirli role sahip kişiler"),
        ("AllContacts", "Firmanın tüm aktif kişileri"), ("CompanyPoints", "Firmanın kendi iletişim noktaları"),
    ];

    private static readonly Dictionary<string, string> Reasons = new()
    {
        ["granted"] = "İzinli",
        ["denied"] = "Pazarlama iletişimi reddedilmiş",
        ["unknown"] = "İzin bilinmiyor (gönderilmez)",
        ["invalid-address"] = "Geçersiz adres",
        ["shared-address"] = "Paylaşılan adres (izin kişiye değil adrese bağlı; otomatik gönderilmez)",
        ["duplicate-address"] = "Aynı adres kitlede zaten var",
        ["duplicate-person"] = "Aynı kişi başka kayıt üzerinden zaten seçildi",
        ["per-target-limit"] = "Hedef başına alıcı sınırı aşıldı",
        ["no-matching-contact"] = "Kurala uyan kişi yok (ör. birincil irtibat tanımlı değil)",
        ["no-contact-point"] = "Seçilen kanalda etkin iletişim noktası yok",
        ["target-not-found"] = "Hedef kayıt bulunamadı ya da erişiminiz yok",
        ["suppressed:hard-bounce"] = "Engelli: kalıcı teslim hatası",
        ["suppressed:invalid-address"] = "Engelli: geçersiz adres",
        ["suppressed:complaint"] = "Engelli: alıcı şikayet etti",
        ["suppressed:opt-out"] = "Engelli: iletişim istemiyor",
        ["suppressed:manual"] = "Engelli: elle engellenmiş",
    };

    public static readonly (string Key, string Label)[] RunStatuses =
    [
        ("Draft", "Taslak"), ("PendingApproval", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Running", "Çalışıyor"), ("Paused", "Duraklatıldı"),
        ("Completed", "Tamamlandı"), ("Cancelled", "İptal edildi"),
    ];

    public static string RunBadge(string status) => status switch
    {
        "Running" or "Completed" => "won", "Cancelled" => "cancelled", "Paused" => "onhold", "PendingApproval" or "Approved" => "negotiation", _ => "lead",
    };

    /// <summary>Teslimat durumu; "Kabul edildi" sağlayıcının iletiyi aldığı anlamına gelir, teslim edildi demek değildir.</summary>
    public static string DeliveryStatus(string status) => status switch
    {
        "Queued" => "Sırada", "Sending" => "İşleniyor", "Accepted" => "Sağlayıcıya kabul edildi", "Failed" => "Başarısız", "Skipped" => "Atlandı",
        "Unknown" => "Sonuç belirsiz", "Cancelled" => "İptal edildi", _ => status,
    };

    private static readonly Dictionary<string, string> DeliveryCodes = new()
    {
        ["address-gone"] = "Adres artık yok (iletişim noktası pasif ya da silinmiş)", ["frequency-limit"] = "Günlük ileti sınırı doldu", ["run-cancelled"] = "Çalıştırma iptal edildi",
        ["address-rejected"] = "Sağlayıcı adresi reddetti", ["rate-limited"] = "Sağlayıcı hız sınırı (geçici hata, deneme hakkı bitti)",
        ["timeout-after-accept"] = "Kabulden sonra yanıt kayboldu; teslim durumu belirsiz", ["stale-sending"] = "İşlem yarıda kaldı; gönderilip gönderilmediği belirsiz",
        ["timeout"] = "Sağlayıcı yanıt vermedi; sonuç belirsiz", ["provider-exception"] = "Sağlayıcı hatası; sonuç belirsiz",
    };

    /// <summary>Teslimat neden kodu: önce teslimata özgü, yoksa hedef kitle/izin nedenleri.</summary>
    public static string DeliveryReason(string code) => DeliveryCodes.TryGetValue(code, out var text) ? text : Reason(code);

    /// <summary>Hedef kitle neden kodunun Türkçe açıklaması; bilinmeyen kod olduğu gibi gösterilir.</summary>
    public static string Reason(string code) => Reasons.GetValueOrDefault(code, code);

    public static string RecipientStatus(string status) => status switch { "Eligible" => "Uygun", "Excluded" => "Elendi", _ => "Alıcı yok" };

    /// <summary>Koşul kurallarını anlatan kısa not (NULL ve gün sınırı davranışı).</summary>
    public const string NullNote = "Boş değerler eşittir, içerir ve aralık koşullarıyla eşleşmez; 'eşit değildir' boş değerleri de kapsar. Tarih sınırları firmanın saat dilimine göredir.";
}
