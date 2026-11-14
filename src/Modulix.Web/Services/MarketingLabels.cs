namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 13); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir
// (anahtardaki `:` yerine `-`, ör. `suppressed:opt-out` → `suppressed-opt-out`). Tablolar özellik olarak tanımlıdır: metinler sınıf yüklenirken değil,
// her okunuşta çözülür (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar eklenir (`MarketingLabelsTests` kapsamı denetler).

/// <summary>Pazarlama ekranlarının etiketleri (segment hedefi, türü, durumu, koşul operatörleri, kampanya, çalıştırma ve teslimat durumları).</summary>
public static class MarketingLabels
{
    public const string TargetGroup = "marketing.target";
    public const string KindGroup = "marketing.kind";
    public const string StatusGroup = "marketing.status";
    public const string OperatorGroup = "marketing.operator";
    public const string ResponseKindGroup = "marketing.response-kind";
    public const string ResponseStatusGroup = "marketing.response-status";
    public const string ChannelGroup = "marketing.channel";
    public const string CampaignStatusGroup = "marketing.campaign-status";
    public const string RecipientRuleGroup = "marketing.recipient-rule";
    public const string RoleGroup = "crm.role";
    public const string ReasonGroup = "marketing.reason";
    public const string RunStatusGroup = "marketing.run-status";
    public const string DeliveryStatusGroup = "marketing.delivery-status";
    public const string DeliveryCodeGroup = "marketing.delivery-code";
    public const string RecipientStatusGroup = "marketing.recipient-status";

    public static string KeyOf(string group, string key) => $"{group}.{key.Replace(':', '-').ToLowerInvariant()}";

    public static string Of(string group, string key, string fallback) => Texts.GetOr(KeyOf(group, key), fallback);

    /// <summary>Tabloyu her okunuşta çevirir.</summary>
    public static IReadOnlyList<(string Key, string Label)> Translate(string group, IReadOnlyList<(string Key, string Label)> source) =>
        [.. source.Select(x => (x.Key, Of(group, x.Key, x.Label)))];

    /// <summary>Tablodaki karşılığı çevirir; tabloda olmayan anahtar <paramref name="otherwise"/> (verilmezse anahtarın kendisi) olarak döner.</summary>
    public static string Lookup(string group, IReadOnlyList<(string Key, string Label)> source, string key, string? otherwise = null)
    {
        foreach (var (k, label) in source)
        {
            if (k == key)
            {
                return Of(group, k, label);
            }
        }

        return otherwise ?? key;
    }

    /// <summary>Sözlükteki yer tutuculu metin; sözlük yüklenmemişse Türkçe kaynak metin kullanılır.</summary>
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Texts.GetOr(key, fallback), args);

    internal static readonly (string Key, string Label)[] TargetSource = [("Company", "Firma"), ("Person", "Kişi"), ("Lead", "Müşteri adayı")];

    internal static readonly (string Key, string Label)[] KindSource = [("Dynamic", "Dinamik (koşuldan)"), ("Static", "Statik (elle seçilen)")];

    internal static readonly (string Key, string Label)[] StatusSource = [("Draft", "Taslak"), ("Active", "Etkin"), ("Archived", "Arşiv")];

    internal static readonly (string Key, string Label)[] OperatorSource =
    [
        ("eq", "eşittir"), ("ne", "eşit değildir (boş dahil)"), ("contains", "içerir"), ("in", "listedekilerden biri"), ("between", "arasında"),
        ("empty", "boş"), ("notempty", "dolu"), ("lastdays", "son N gün içinde"), ("nextdays", "önümüzdeki N gün içinde"), ("before", "öncesinde"),
        ("after", "sonrasında"), ("exists", "var"), ("notexists", "yok"),
    ];

    internal static readonly (string Key, string Label)[] ResponseKindSource =
    [
        ("Interested", "İlgilendi"), ("Question", "Soru"), ("MeetingRequest", "Görüşme talebi"), ("NotInterested", "İlgilenmiyor"),
        ("OptOut", "İletişim istemiyor"), ("Complaint", "Şikâyet"), ("Other", "Diğer"),
    ];

    internal static readonly (string Key, string Label)[] ResponseStatusSource =
        [("New", "Yeni"), ("InProgress", "İşlemde"), ("Converted", "Müşteri adayına dönüştü"), ("Closed", "Kapalı")];

    internal static readonly (string Key, string Label)[] ChannelSource = [("Email", "E-posta"), ("Phone", "Telefon"), ("Sms", "SMS"), ("WhatsApp", "WhatsApp")];

    internal static readonly (string Key, string Label)[] CampaignStatusSource = [("Draft", "Taslak"), ("Archived", "Arşiv")];

    internal static readonly (string Key, string Label)[] RecipientRuleSource =
    [
        ("PrimaryContact", "Birincil irtibat kişisi"), ("DecisionMaker", "Karar verici kişiler"), ("Role", "Belirli role sahip kişiler"),
        ("AllContacts", "Firmanın tüm aktif kişileri"), ("CompanyPoints", "Firmanın kendi iletişim noktaları"),
    ];

    // Firma kişisinin rolü; anahtarlar CRM'in kişi rolleriyle aynıdır ve metinler CRM sözlüğünden (`crm.role.*`) gelir.
    internal static readonly (string Key, string Label)[] RoleSource =
        [("Purchasing", "Satın alma"), ("Finance", "Finans"), ("Technical", "Teknik"), ("Management", "Yönetim"), ("Other", "Diğer")];

    internal static readonly (string Key, string Label)[] ReasonSource =
    [
        ("granted", "İzinli"), ("denied", "Pazarlama iletişimi reddedilmiş"), ("unknown", "İzin bilinmiyor (gönderilmez)"),
        ("topic-opt-out", "Alıcı kampanyanın konusundan çıkmış"), ("invalid-address", "Geçersiz adres"),
        ("shared-address", "Paylaşılan adres (izin kişiye değil adrese bağlı; otomatik gönderilmez)"), ("duplicate-address", "Aynı adres kitlede zaten var"),
        ("duplicate-person", "Aynı kişi başka kayıt üzerinden zaten seçildi"), ("per-target-limit", "Hedef başına alıcı sınırı aşıldı"),
        ("no-matching-contact", "Kurala uyan kişi yok (ör. birincil irtibat tanımlı değil)"), ("no-contact-point", "Seçilen kanalda etkin iletişim noktası yok"),
        ("target-not-found", "Hedef kayıt bulunamadı ya da erişiminiz yok"), ("suppressed:hard-bounce", "Engelli: kalıcı teslim hatası"),
        ("suppressed:invalid-address", "Engelli: geçersiz adres"), ("suppressed:complaint", "Engelli: alıcı şikayet etti"),
        ("suppressed:opt-out", "Engelli: iletişim istemiyor"), ("suppressed:manual", "Engelli: elle engellenmiş"),
    ];

    internal static readonly (string Key, string Label)[] RunStatusSource =
    [
        ("Draft", "Taslak"), ("PendingApproval", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Running", "Çalışıyor"), ("Paused", "Duraklatıldı"),
        ("Completed", "Tamamlandı"), ("Cancelled", "İptal edildi"),
    ];

    internal static readonly (string Key, string Label)[] DeliveryStatusSource =
    [
        ("Queued", "Sırada"), ("Sending", "İşleniyor"), ("Accepted", "Sağlayıcıya kabul edildi"), ("Failed", "Başarısız"), ("Skipped", "Atlandı"),
        ("Unknown", "Sonuç belirsiz"), ("Cancelled", "İptal edildi"),
    ];

    internal static readonly (string Key, string Label)[] DeliveryCodeSource =
    [
        ("address-gone", "Adres artık yok (iletişim noktası pasif ya da silinmiş)"), ("frequency-limit", "Günlük ileti sınırı doldu"),
        ("run-cancelled", "Çalıştırma iptal edildi"), ("address-rejected", "Sağlayıcı adresi reddetti"),
        ("rate-limited", "Sağlayıcı hız sınırı (geçici hata, deneme hakkı bitti)"), ("timeout-after-accept", "Kabulden sonra yanıt kayboldu; teslim durumu belirsiz"),
        ("stale-sending", "İşlem yarıda kaldı; gönderilip gönderilmediği belirsiz"), ("timeout", "Sağlayıcı yanıt vermedi; sonuç belirsiz"),
        ("provider-exception", "Sağlayıcı hatası; sonuç belirsiz"),
    ];

    internal static readonly (string Key, string Label)[] RecipientStatusSource = [("Eligible", "Uygun"), ("Excluded", "Elendi"), ("Unresolved", "Alıcı yok")];

    public static IReadOnlyList<(string Key, string Label)> Targets => Translate(TargetGroup, TargetSource);

    public static IReadOnlyList<(string Key, string Label)> Kinds => Translate(KindGroup, KindSource);

    public static IReadOnlyList<(string Key, string Label)> Statuses => Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> Operators => Translate(OperatorGroup, OperatorSource);

    public static IReadOnlyList<(string Key, string Label)> ResponseKinds => Translate(ResponseKindGroup, ResponseKindSource);

    public static IReadOnlyList<(string Key, string Label)> ResponseStatuses => Translate(ResponseStatusGroup, ResponseStatusSource);

    public static IReadOnlyList<(string Key, string Label)> Channels => Translate(ChannelGroup, ChannelSource);

    public static IReadOnlyList<(string Key, string Label)> CampaignStatuses => Translate(CampaignStatusGroup, CampaignStatusSource);

    public static IReadOnlyList<(string Key, string Label)> RecipientRules => Translate(RecipientRuleGroup, RecipientRuleSource);

    public static IReadOnlyList<(string Key, string Label)> Roles => Translate(RoleGroup, RoleSource);

    public static IReadOnlyList<(string Key, string Label)> Reasons => Translate(ReasonGroup, ReasonSource);

    public static IReadOnlyList<(string Key, string Label)> RunStatuses => Translate(RunStatusGroup, RunStatusSource);

    public static IReadOnlyList<(string Key, string Label)> DeliveryStatuses => Translate(DeliveryStatusGroup, DeliveryStatusSource);

    public static IReadOnlyList<(string Key, string Label)> DeliveryCodes => Translate(DeliveryCodeGroup, DeliveryCodeSource);

    public static IReadOnlyList<(string Key, string Label)> RecipientStatuses => Translate(RecipientStatusGroup, RecipientStatusSource);

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string key) => list.FirstOrDefault(i => i.Key == key).Label ?? key;

    public static string ResponseKind(string kind) => Lookup(ResponseKindGroup, ResponseKindSource, kind);

    public static string ResponseStatus(string status) => Lookup(ResponseStatusGroup, ResponseStatusSource, status);

    public static string Operator(string key) => Lookup(OperatorGroup, OperatorSource, key);

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

    public static string RunBadge(string status) => status switch
    {
        "Running" or "Completed" => "won", "Cancelled" => "cancelled", "Paused" => "onhold", "PendingApproval" or "Approved" => "negotiation", _ => "lead",
    };

    /// <summary>Teslimat durumu; "Kabul edildi" sağlayıcının iletiyi aldığı anlamına gelir, teslim edildi demek değildir.</summary>
    public static string DeliveryStatus(string status) => Lookup(DeliveryStatusGroup, DeliveryStatusSource, status);

    /// <summary>Teslimat neden kodu: önce teslimata özgü, yoksa hedef kitle/izin nedenleri.</summary>
    public static string DeliveryReason(string code) => Lookup(DeliveryCodeGroup, DeliveryCodeSource, code, Reason(code));

    /// <summary>Hedef kitle neden kodunun açıklaması; bilinmeyen kod olduğu gibi gösterilir.</summary>
    public static string Reason(string code) => Lookup(ReasonGroup, ReasonSource, code);

    /// <summary>Dondurulmuş alıcının durumu; tanınmayan durum "Alıcı yok" sayılır.</summary>
    public static string RecipientStatus(string status) =>
        Lookup(RecipientStatusGroup, RecipientStatusSource, status, Lookup(RecipientStatusGroup, RecipientStatusSource, "Unresolved"));

    /// <summary>Koşul kurallarını anlatan kısa not (NULL ve gün sınırı davranışı).</summary>
    public static string NullNote => Texts.GetOr(
        "marketing.nullNote",
        "Boş değerler eşittir, içerir ve aralık koşullarıyla eşleşmez; 'eşit değildir' boş değerleri de kapsar. Tarih sınırları firmanın saat dilimine göredir.");
}
