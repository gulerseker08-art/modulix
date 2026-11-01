namespace Modulix.Modules.Orders.Lifecycle;

public static class HandoverPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static readonly IReadOnlyList<string> All = [Low, Normal, High, Urgent];
}

/// <summary>Devir eylemleri; adlar API yoludur (<c>POST /api/orders/handovers/{id}/{eylem}</c>).</summary>
public static class HandoverAction
{
    public const string Send = "send";
    public const string RequestInfo = "request-info";
    public const string Accept = "accept";

    public static readonly IReadOnlyList<string> All = [Send, RequestInfo, Accept];
}

/// <summary>Devir geçmişi türleri. Yeni tür eklemek serbesttir; eski satırlar aynen okunur.</summary>
public static class HandoverEventKinds
{
    public const string Created = "Created";
    public const string Sent = "Sent";
    public const string Resubmitted = "Resubmitted";
    public const string InfoRequested = "InfoRequested";
    public const string Accepted = "Accepted";
    public const string WaitingEscalated = "WaitingEscalated";
}

/// <summary>Hazırlık denetimi için devir içeriği (yalnızca zorunlu bilgi denetiminin baktığı alanlar).</summary>
public sealed record HandoverSnapshot(
    string? Scope, string? Deliverables, string? Location, string? ContactName, string? ContactPhone, string? ContactEmail, DateOnly? TargetStartDate,
    DateOnly? TargetEndDate, string? CompletionCriteria, string? AcceptanceCriteria, Guid? ResponsibleUserId, string? ResponsibleTeam);

public sealed record HandoverChecklistState(string Text, bool IsRequired, bool IsDone);

/// <summary>Devir kuralları (saf): hazırlık denetimi, düzenlenebilirlik ve eylem geçişleri.</summary>
public static class HandoverRules
{
    /// <summary>Sistemin her devir kaydına açtığı kontrol listesi.</summary>
    public static readonly IReadOnlyList<string> DefaultChecklist =
    [
        "Satılan kapsam ve kapsam dışı işler netleştirildi",
        "Teslim edilecek çıktılar ve miktarlar kontrol edildi",
        "Müşteriye verilen taahhütler operasyona aktarıldı",
        "Lokasyon, ilgili kişi ve iletişim bilgileri doğrulandı",
        "Gerekli belgeler ve müşteri ön koşulları listelendi",
        "Ödeme ve sözleşme onayı koşulları (varsa) belirtildi",
        "Riskler, özel talepler ve bağımlılıklar yazıldı",
        "Tamamlanma ve müşteri kabul kriterleri yazıldı",
    ];

    /// <summary>Devir yalnızca bu durumlarda düzenlenir; gönderilen ve kabul edilen devir değişmez.</summary>
    public static bool CanEdit(string status) => status is HandoverStatus.Preparing or HandoverStatus.Ready or HandoverStatus.InfoRequested;

    /// <summary>
    /// Zorunlu bilgi ve belge denetimi: gönderimi engelleyen eksiklerin listesi (boşsa devir hazırdır). Sistem bu denetimi her kayıtta ve gönderimde
    /// sunucuda yapar; arayüz yalnızca aynı listeyi gösterir.
    /// </summary>
    public static IReadOnlyList<string> Missing(HandoverSnapshot h, IReadOnlyList<HandoverChecklistState> items)
    {
        var missing = new List<string>();
        void Need(string? value, string what)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(what);
            }
        }

        Need(h.Scope, "Satılan kapsam yazılmadı.");
        Need(h.Deliverables, "Teslim edilecek çıktılar yazılmadı.");
        Need(h.Location, "Lokasyon yazılmadı.");
        Need(h.ContactName, "Müşteri tarafı ilgili kişi yazılmadı.");
        if (string.IsNullOrWhiteSpace(h.ContactPhone) && string.IsNullOrWhiteSpace(h.ContactEmail))
        {
            missing.Add("İlgili kişi için telefon ya da e-posta girilmedi.");
        }

        if (h.TargetStartDate is null)
        {
            missing.Add("Hedef başlangıç tarihi girilmedi.");
        }

        if (h.TargetStartDate is { } start && h.TargetEndDate is { } end && end < start)
        {
            missing.Add("Hedef bitiş tarihi başlangıçtan önce olamaz.");
        }

        Need(h.CompletionCriteria, "Tamamlanma kriterleri yazılmadı.");
        Need(h.AcceptanceCriteria, "Müşteri kabul kriterleri yazılmadı.");
        if (h.ResponsibleUserId is null && string.IsNullOrWhiteSpace(h.ResponsibleTeam))
        {
            missing.Add("Sorumlu operasyon kullanıcısı ya da ekibi seçilmedi.");
        }

        foreach (var item in items.Where(i => i.IsRequired && !i.IsDone))
        {
            missing.Add($"Kontrol listesi maddesi tamamlanmadı: {item.Text}");
        }

        return missing;
    }

    /// <summary>Düzenlenebilir devrin durumu: eksik yoksa Hazır, varsa Hazırlanıyor; bilgi bekleyen devir yeniden gönderilene kadar o durumda kalır.</summary>
    public static string EditableStatus(string current, IReadOnlyList<string> missing) =>
        current == HandoverStatus.InfoRequested ? current : missing.Count == 0 ? HandoverStatus.Ready : HandoverStatus.Preparing;

    /// <summary>Eylem geçiş kuralı. <paramref name="orderStatus"/>: siparişin ticari durumu (devir yalnızca onaylı siparişten gönderilir).</summary>
    public static OrderRuleResult Can(string action, string status, string orderStatus, IReadOnlyList<string> missing)
    {
        switch (action)
        {
            case HandoverAction.Send:
                if (orderStatus != OrderStatus.Approved)
                {
                    return OrderRuleResult.Deny("order-not-approved", "Devir yalnızca onaylanmış siparişten operasyona gönderilebilir.");
                }

                if (status is HandoverStatus.Sent)
                {
                    return OrderRuleResult.Deny("already-sent", "Devir zaten operasyona gönderilmiş.");
                }

                if (status is HandoverStatus.Accepted)
                {
                    return OrderRuleResult.Deny("accepted", "Devir operasyon tarafından kabul edilmiş.");
                }

                return missing.Count == 0
                    ? OrderRuleResult.Ok
                    : OrderRuleResult.Deny("missing-info", "Zorunlu bilgi ya da belgeler eksik: " + missing[0] + (missing.Count > 1 ? $" (+{missing.Count - 1} eksik daha)" : string.Empty));

            case HandoverAction.RequestInfo:
            case HandoverAction.Accept:
                return status == HandoverStatus.Sent
                    ? OrderRuleResult.Ok
                    : OrderRuleResult.Deny("not-sent", "Yalnızca operasyona gönderilmiş ve henüz karara bağlanmamış devir için yapılabilir.");

            default:
                return OrderRuleResult.Deny("unknown", "Bilinmeyen eylem.");
        }
    }

    /// <summary>Gerekçe/not zorunlu eylemler: bilgi isteği her zaman, bilgi isteği sonrası yeniden gönderim (neyin tamamlandığı).</summary>
    public static bool RequiresNote(string action, string status) =>
        action == HandoverAction.RequestInfo || (action == HandoverAction.Send && status == HandoverStatus.InfoRequested);
}
