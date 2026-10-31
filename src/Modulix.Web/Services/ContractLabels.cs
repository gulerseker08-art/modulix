using System.Globalization;
using Modulix.Shared.Contracts;

namespace Modulix.Web.Services;

/// <summary>Sözleşme durumlarının Türkçe etiketleri ve rozet renkleri (ADR-0021); anahtarlar sunucudaki sabitlerle aynıdır.</summary>
public static class ContractLabels
{
    public static IReadOnlyList<(string Key, string Label)> Preparations { get; } =
        [("Draft", "Taslak"), ("InReview", "İncelemede"), ("ReadyToPublish", "Yayıma hazır")];

    public static IReadOnlyList<(string Key, string Label)> Approvals { get; } =
    [
        ("NotRequired", "Onay gerekmiyor"), ("Pending", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Rejected", "Onay reddedildi"), ("Withdrawn", "Onay geri çekildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Signatures { get; } =
    [
        ("NotSent", "İmzaya gönderilmedi"), ("Pending", "İmza bekliyor"), ("PartiallySigned", "Kısmen imzalandı"), ("Completed", "İmzalandı"),
        ("Rejected", "İmza reddedildi"), ("Expired", "İmza süresi doldu"), ("Cancelled", "İmza iptal edildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Effectives { get; } =
    [
        ("NotInForce", "Yürürlükte değil"), ("AwaitingStart", "Başlangıcı bekleniyor"), ("Active", "Aktif"), ("Suspended", "Askıda"),
        ("Expired", "Süresi doldu"), ("Terminated", "Feshedildi"), ("Cancelled", "İptal edildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> RenewalModes { get; } =
        [("None", "Yenilemesiz"), ("Manual", "Manuel yenileme"), ("Automatic", "Otomatik yenileme")];

    public static IReadOnlyList<(string Key, string Label)> Confidentialities { get; } =
        [("Internal", "Kurum içi"), ("Confidential", "Gizli"), ("Restricted", "Kısıtlı")];

    public static IReadOnlyList<(string Key, string Label)> RiskLevels { get; } =
        [("Low", "Düşük"), ("Medium", "Orta"), ("High", "Yüksek")];

    public static IReadOnlyList<(string Key, string Label)> LineKinds { get; } =
        [("Service", "Hizmet"), ("Product", "Ürün"), ("Text", "Açıklama satırı")];

    public static IReadOnlyList<(string Key, string Label)> Periods { get; } =
        [("", "Tek seferlik"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public static IReadOnlyList<(string Key, string Label)> PartyRoles => ContractPartyRoles.All;

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string EffectiveBadge(string status) => status switch
    {
        "Active" => "won",
        "AwaitingStart" or "Suspended" => "onhold",
        "Expired" => "lost",
        "Terminated" or "Cancelled" => "cancelled",
        _ => "open",
    };

    public static string ApprovalBadge(string status) => status switch
    {
        "Approved" => "won",
        "Pending" => "onhold",
        "Rejected" => "lost",
        _ => "open",
    };

    public static string SignatureBadge(string status) => status switch
    {
        "Completed" => "won",
        "Pending" or "PartiallySigned" => "onhold",
        "Rejected" or "Expired" => "lost",
        "Cancelled" => "cancelled",
        _ => "open",
    };

    /// <summary>Tutar; izin yoksa (null) gizli olduğu söylenir, para birimleri karıştırılmaz.</summary>
    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{value.ToString("N2", Regional.Numbers)} {currency}" : "Gizli";

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, CultureInfo.InvariantCulture) : "—";

    /// <summary>Bitişe kalan gün (bugüne göre, tenant saat dilimi); bitişsizde null.</summary>
    public static int? DaysUntil(DateOnly? date) =>
        date is { } d ? d.DayNumber - DateOnly.FromDateTime(Regional.Now.DateTime).DayNumber : null;
}

/// <summary>Eylem ve tarihçe etiketleri (dilim 2); anahtarlar sunucudaki <c>ContractAction</c> / <c>ContractEventKinds</c> ile aynıdır.</summary>
public static class ContractActionLabels
{
    public static string Action(string key) => key switch
    {
        "send-to-review" => "İncelemeye gönder",
        "mark-ready" => "Yayıma hazır işaretle",
        "reopen-draft" => "Taslağa dön",
        "submit-for-approval" => "Onaya gönder",
        "withdraw-approval" => "Onayı geri çek",
        "approve" => "Onayla",
        "reject-approval" => "Onayı reddet",
        "return-for-changes" => "Değişiklik için geri gönder",
        "cancel" => "Sözleşmeyi iptal et",
        "revise" => "Revizyon aç",
        _ => key,
    };

    public static bool IsPrimary(string key) => key is "submit-for-approval" or "approve" or "mark-ready";

    public static string History(string kind) => kind switch
    {
        "Created" => "Oluşturuldu",
        "CreatedFromQuote" => "Kabul edilmiş tekliften oluşturuldu",
        "DocumentGenerated" => "Belge üretildi",
        "SentToReview" => "İncelemeye gönderildi",
        "MarkedReady" => "Yayıma hazır işaretlendi",
        "ReopenedDraft" => "Taslağa döndü",
        "SubmittedForApproval" => "Onaya gönderildi (sürüm donduruldu)",
        "ApprovalWithdrawn" => "Onay geri çekildi",
        "Approved" => "Onaylandı",
        "ApprovalRejected" => "Onay reddedildi",
        "ApprovalStepApproved" => "Onay adımı onaylandı (sıradaki adım açıldı)",
        "ApprovalReturned" => "Onaydan değişiklik talebiyle geri gönderildi; yeni revizyon taslağı açıldı",
        "ApprovalInvalidated" => "Taslağa döndü; önceki onay geçersiz kaldı",
        "Cancelled" => "İptal edildi",
        "Revised" => "Yeni revizyon açıldı",
        _ => kind,
    };

    public static IReadOnlyList<(string Key, string Label)> VersionStatuses { get; } =
    [
        ("Frozen", "Donduruldu"), ("Approved", "Onaylandı"), ("Rejected", "Reddedildi"), ("Withdrawn", "Geri çekildi"), ("Signed", "İmzalandı"), ("Superseded", "Geçersiz (yeni revizyon)"),
    ];

    public static string Field(string key) => key switch
    {
        "title" => "Başlık", "type" => "Tür", "description" => "Açıklama", "partyId" => "Müşteri", "personId" => "İlgili kişi", "startDate" => "Başlangıç",
        "endDate" => "Bitiş", "isIndefinite" => "Belirsiz süreli", "currency" => "Para birimi", "priceIncludesTax" => "Vergi dahil", "contractValue" => "Sözleşme bedeli",
        "paymentTerms" => "Ödeme koşulları", "renewalMode" => "Yenileme yöntemi", "renewalPeriodMonths" => "Yenileme süresi (ay)", "noticePeriodDays" => "Bildirim süresi (gün)",
        "noticeDeadline" => "Son bildirim tarihi", "confidentiality" => "Gizlilik", "hasNonStandardTerms" => "Standart dışı madde", "riskLevel" => "Risk düzeyi", "netTotal" => "Net toplam", "taxTotal" => "Vergi", "grossTotal" => "Genel toplam",
        "kind" => "Tür", "productCode" => "Kod", "name" => "Ad", "quantity" => "Miktar", "unitName" => "Birim", "unitPrice" => "Birim fiyat", "discountPercent" => "İndirim %",
        "taxRatePercent" => "Vergi %", "billingPeriod" => "Dönem", "net" => "Net", "tax" => "Vergi", "gross" => "Toplam", "role" => "Rol", "notes" => "Not",
        _ => key,
    };

    public static string Change(string key) => key switch { "Added" => "Eklendi", "Removed" => "Kaldırıldı", "Changed" => "Değişti", _ => key };
}

/// <summary>Şablon durum ve eylem etiketleri (dilim 4); anahtarlar sunucudaki <c>TemplateStatus</c> ile aynıdır.</summary>
public static class TemplateLabels
{
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
        [("Draft", "Taslak"), ("Published", "Yayımlanmış"), ("Archived", "Arşivlenmiş")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string StatusBadge(string status) => status switch { "Published" => "won", "Archived" => "cancelled", _ => "open" };

    public static string Action(string? key) => key switch
    {
        "publish" => "Yayımla",
        "archive" => "Arşivle",
        "restore" => "Geri al",
        _ => key ?? string.Empty,
    };
}

/// <summary>İmza etiketleri (dilim 6); anahtarlar sunucudaki <c>SignatureRequestStatus</c>, <c>SignatoryStatus</c> ve <c>SignatureAssurance</c> ile aynıdır.</summary>
public static class SignatureLabels
{
    public static IReadOnlyList<(string Key, string Label)> RequestStatuses { get; } =
    [
        ("Pending", "İmza bekliyor"), ("PartiallySigned", "Kısmen imzalandı"), ("Completed", "Tamamlandı"), ("Rejected", "Reddedildi"), ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"),
    ];

    public static IReadOnlyList<(string Key, string Label)> SignatoryStatuses { get; } =
    [
        ("Waiting", "Sırası gelmedi"), ("Sent", "İmza bekliyor"), ("Viewed", "Görüntüledi"), ("Signed", "İmzaladı"), ("Declined", "Reddetti"), ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"),
    ];

    /// <summary>Güvence düzeyleri birbirine eşit gösterilmez; her biri kendi adıyla ve sınırıyla anılır.</summary>
    public static IReadOnlyList<(string Key, string Label)> Assurances { get; } =
    [
        ("ManualScan", "Islak imza taraması (yetkili doğruladı)"),
        ("SimulatedElectronic", "Simülasyon (hukuki değeri yok)"),
        ("ProviderElectronic", "Sağlayıcı: elektronik imza"),
        ("ProviderQualified", "Sağlayıcı: nitelikli elektronik imza (sağlayıcı bildirimi)"),
    ];

    public static IReadOnlyList<(string Key, string Label)> EventTypes { get; } =
    [
        ("Requested", "İstek açıldı"), ("Sent", "İmzaya sunuldu"), ("Viewed", "Görüntülendi"), ("Signed", "İmzalandı"), ("Declined", "Reddedildi"), ("Reminded", "Hatırlatma kaydedildi"),
        ("Expired", "Süre doldu"), ("Cancelled", "İptal edildi"), ("Completed", "Tamamlandı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Outcomes { get; } =
        [("Applied", "Uygulandı"), ("Duplicate", "Yinelenen"), ("OutOfOrder", "Sırası bozuk"), ("Rejected", "Reddedildi"), ("Ignored", "Yok sayıldı")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string Badge(string status) => status switch
    {
        "Completed" or "Signed" => "won",
        "Pending" or "PartiallySigned" or "Sent" or "Viewed" => "onhold",
        "Rejected" or "Declined" or "Expired" => "lost",
        "Cancelled" => "cancelled",
        _ => "open",
    };
}

/// <summary>Taahhüt etiketleri (dilim 7); anahtarlar sunucudaki <c>CommitmentKind</c>, <c>OccurrenceStatus</c> ve <c>FollowUpState</c> ile aynıdır.</summary>
public static class CommitmentLabels
{
    public static IReadOnlyList<(string Key, string Label)> Obligors { get; } = [("Provider", "Hizmet veren (biz)"), ("Customer", "Müşteri"), ("Both", "Her iki taraf")];

    public static IReadOnlyList<(string Key, string Label)> Kinds { get; } = [("OneTime", "Tek seferlik"), ("Periodic", "Dönemsel")];

    public static IReadOnlyList<(string Key, string Label)> Recurrences { get; } = [("Weekly", "Haftalık"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public static IReadOnlyList<(string Key, string Label)> Directions { get; } = [("AtLeast", "En az"), ("AtMost", "En çok"), ("Exactly", "Tam (toleransla)")];

    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } = [("Active", "Etkin"), ("Completed", "Tamamlandı"), ("Cancelled", "İptal edildi")];

    public static IReadOnlyList<(string Key, string Label)> OccurrenceStatuses { get; } =
        [("Open", "Açık"), ("Fulfilled", "Yerine getirildi"), ("Breached", "İhlal"), ("Waived", "Muaf"), ("Cancelled", "İptal edildi")];

    public static IReadOnlyList<(string Key, string Label)> FollowUps { get; } =
        [("None", "Takip aktivitesi yok"), ("Created", "Takip aktivitesi açıldı"), ("Unavailable", "Aktivite modülü kapalı"), ("Failed", "Takip aktivitesi açılamadı (yeniden denenecek)")];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string Badge(string status, bool overdue = false) => overdue ? "lost" : status switch
    {
        "Fulfilled" or "Completed" => "won",
        "Breached" => "lost",
        "Open" or "Active" => "onhold",
        "Cancelled" or "Waived" => "cancelled",
        _ => "open",
    };

    /// <summary>Hedef ve ölçü gösterimi (ör. "en az 99,5 %").</summary>
    public static string Target(string? metric, string? direction, decimal? target, decimal? tolerance, string? unit) =>
        metric is null || target is null
            ? "—"
            : $"{metric}: {Label(Directions, direction).ToLowerInvariant()} {target.Value.ToString("0.######", Regional.Numbers)}{(string.IsNullOrEmpty(unit) ? string.Empty : " " + unit)}{(tolerance is > 0 ? $" (±{tolerance.Value.ToString("0.######", Regional.Numbers)})" : string.Empty)}";
}
