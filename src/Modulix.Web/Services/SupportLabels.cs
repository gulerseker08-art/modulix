namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 12); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir
// (sıralama anahtarındaki baştaki `-` yerine `desc-`). Tablolar özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür
// (sözlük sonradan yüklenir). Yeni değer eklenince üç sözlüğe de anahtar eklenir (`SupportLabelsTests` kapsamı denetler).

/// <summary>Destek ekranlarının etiketleri; anahtarlar sunucudaki <c>TicketStatus</c>, <c>TicketPriority</c> ve eylem adlarıyla aynıdır.</summary>
public static class SupportLabels
{
    public const string TicketTypeList = "support.ticket-type";
    public const string ResolutionCodeList = "support.resolution-code";
    public const string ComplaintCategory = "complaint";

    public const string StatusGroup = "support.status";
    public const string PriorityGroup = "support.priority";
    public const string LevelGroup = "support.level";
    public const string ChannelGroup = "support.channel";
    public const string SortGroup = "support.sort";
    public const string MessageKindGroup = "support.message-kind";
    public const string ActionGroup = "support.action";
    public const string HistoryGroup = "support.history";
    public const string DeliveryGroup = "support.delivery";

    public static string KeyOf(string group, string key) => $"{group}.{(key.StartsWith('-') ? "desc-" + key[1..] : key).ToLowerInvariant()}";

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

    internal static readonly (string Key, string Label)[] StatusSource =
    [
        ("New", "Yeni"), ("Triage", "Sınıflandırmada"), ("Assigned", "Atandı"), ("InProgress", "İşlemde"), ("WaitingCustomer", "Müşteriden bilgi bekleniyor"),
        ("WaitingInternal", "İç ekip bekleniyor"), ("Resolved", "Çözüldü"), ("Closed", "Kapandı"), ("Reopened", "Yeniden açıldı"), ("Cancelled", "İptal edildi"), ("Merged", "Birleştirildi"),
    ];

    internal static readonly (string Key, string Label)[] PrioritySource =
        [("Low", "Düşük"), ("Normal", "Normal"), ("High", "Yüksek"), ("Critical", "Kritik")];

    internal static readonly (string Key, string Label)[] LevelSource = [("Low", "Düşük"), ("Medium", "Orta"), ("High", "Yüksek")];

    internal static readonly (string Key, string Label)[] ChannelSource =
        [("User", "Kullanıcı girişi"), ("Phone", "Telefon"), ("Email", "E-posta"), ("Web", "Web formu"), ("Portal", "Müşteri portalı"), ("Api", "API")];

    internal static readonly (string Key, string Label)[] SortSource =
    [
        ("-createdAt", "En yeni"), ("createdAt", "En eski"), ("priority", "Öncelik (yüksek önce)"), ("-lastActivity", "Son hareket"), ("resolutionDue", "Çözüm hedefi (yakın)"), ("firstResponseDue", "İlk yanıt hedefi (yakın)"),
        ("number", "Numara (artan)"), ("-number", "Numara (azalan)"), ("subject", "Konu (A-Z)"),
    ];

    internal static readonly (string Key, string Label)[] MessageKindSource =
        [("reply", "Müşteriye yanıt"), ("note", "İç not (müşteriye gitmez)"), ("customer-message", "Müşteriden gelen mesajı kaydet"), ("customer-confirmation", "Müşteri çözümü onayladı (talebi kapatır)")];

    internal static readonly (string Key, string Label)[] ActionSource =
    [
        ("triage", "Sınıflandırmaya al"), ("assign", "Ata"), ("take", "Üzerime al"), ("transfer", "Başka kuyruğa devret"), ("start", "İşleme al"),
        ("wait-customer", "Müşteriden bilgi bekle"), ("wait-internal", "İç ekibi bekle"), ("resume", "Sürdür"), ("resolve", "Çöz"), ("close", "Kapat"),
        ("reopen", "Yeniden aç"), ("cancel", "İptal et"), ("set-priority", "Önceliği değiştir"),
    ];

    internal static readonly (string Key, string Label)[] HistorySource =
    [
        ("Created", "Talep açıldı"), ("Triaged", "Sınıflandırmaya alındı"), ("Assigned", "Atama yapıldı"), ("Taken", "Üzerine alındı"),
        ("Transferred", "Başka kuyruğa devredildi"), ("Started", "İşleme alındı"), ("WaitingCustomer", "Müşteriden bilgi beklenmeye başlandı"),
        ("WaitingInternal", "İç ekip beklenmeye başlandı"), ("Resumed", "Sürdürüldü"), ("Resolved", "Çözüldü"), ("Closed", "Kapatıldı"),
        ("Reopened", "Yeniden açıldı"), ("Cancelled", "İptal edildi"), ("PriorityChanged", "Öncelik elle değiştirildi"),
        ("CustomerLinked", "Müşteri/kişi bağlantısı değişti"), ("PublicReply", "Müşteriye yanıt yazıldı"), ("CustomerMessage", "Müşteri mesajı kaydedildi"),
        ("InternalNote", "İç not eklendi"), ("RoutedByRule", "Yönlendirme kuralıyla yönlendirildi"), ("MergedInto", "Başka talebe birleştirildi"),
        ("MergeReceived", "Birleştirilen talep alındı"), ("TaskAdded", "Alt iş eklendi"), ("TaskCompleted", "Alt iş tamamlandı"),
        ("TaskReopened", "Alt iş yeniden açıldı"), ("CustomerConfirmed", "Müşteri çözümü onayladı"), ("AutoClosed", "Zaman aşımıyla otomatik kapatıldı"),
        ("CorrectiveActionAdded", "Düzeltici aksiyon eklendi"), ("CorrectiveActionDone", "Düzeltici aksiyon tamamlandı"),
        ("CorrectiveActionCancelled", "Düzeltici aksiyon iptal edildi"), ("CorrectiveActionReopened", "Düzeltici aksiyon yeniden açıldı"),
        ("LinkAdded", "İlişkili kayıt bağlandı"), ("LinkRemoved", "İlişkili kayıt bağlantısı kaldırıldı"), ("AttachmentAdded", "Ek eklendi"),
        ("InternalAttachmentAdded", "İç nota ek eklendi"), ("SurveyAnswered", "Memnuniyet anketi yanıtlandı"),
        ("LowSatisfaction", "Düşük memnuniyet puanı alındı (takip gerekir)"),
    ];

    internal static readonly (string Key, string Label)[] DeliverySource =
    [
        ("Queued", "E-posta kuyruğuna alındı (müşteriye ulaştığı doğrulanmadı)"), ("Sent", "Gönderildi"), ("Failed", "Gönderilemedi"),
        ("Recorded", "Kaydedildi (e-posta gönderilmedi)"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Statuses => Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> Priorities => Translate(PriorityGroup, PrioritySource);

    public static IReadOnlyList<(string Key, string Label)> Levels => Translate(LevelGroup, LevelSource);

    public static IReadOnlyList<(string Key, string Label)> Channels => Translate(ChannelGroup, ChannelSource);

    public static IReadOnlyList<(string Key, string Label)> Sorts => Translate(SortGroup, SortSource);

    public static IReadOnlyList<(string Key, string Label)> MessageKinds => Translate(MessageKindGroup, MessageKindSource);

    public static IReadOnlyList<(string Key, string Label)> Actions => Translate(ActionGroup, ActionSource);

    public static IReadOnlyList<(string Key, string Label)> Histories => Translate(HistoryGroup, HistorySource);

    public static IReadOnlyList<(string Key, string Label)> Deliveries => Translate(DeliveryGroup, DeliverySource);

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

    public static string Action(string key) => Lookup(ActionGroup, ActionSource, key);

    public static bool IsPrimary(string key) => key is "take" or "start" or "resolve" or "close";

    public static bool IsDanger(string key) => key is "cancel";

    public static string History(string kind) => Lookup(HistoryGroup, HistorySource, kind);

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static string Delivery(string status) => Lookup(DeliveryGroup, DeliverySource, status, string.Empty);

    /// <summary>"3 gün" / "5 sa" / "12 dk" biçiminde yaş (Talepler listesi).</summary>
    public static string Age(DateTimeOffset createdAt, DateTimeOffset now)
    {
        TimeSpan age = now - createdAt;
        return age.TotalDays >= 1 ? Format("support.duration.days", "{0} gün", (int)age.TotalDays)
            : age.TotalHours >= 1 ? Format("support.duration.hours", "{0} sa", (int)age.TotalHours)
            : Format("support.duration.minutes", "{0} dk", Math.Max(0, (int)age.TotalMinutes));
    }

    /// <summary>Sözlükteki yer tutuculu metin; sözlük yüklenmemişse Türkçe kaynak metin kullanılır.</summary>
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Texts.GetOr(key, fallback), args);
}
