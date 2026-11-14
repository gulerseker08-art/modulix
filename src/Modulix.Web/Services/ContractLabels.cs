using System.Globalization;
using Modulix.Shared.Contracts;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 10a); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Tablolar özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir).

/// <summary>Sözleşme durumlarının etiketleri ve rozet renkleri (ADR-0021); anahtarlar sunucudaki sabitlerle aynıdır.</summary>
public static class ContractLabels
{
    public const string PreparationGroup = "contracts.preparation-status";
    public const string ApprovalGroup = "contracts.approval-status";
    public const string SignatureGroup = "contracts.signature-status";
    public const string EffectiveGroup = "contracts.effective-status";
    public const string RenewalModeGroup = "contracts.renewal-mode";
    public const string ConfidentialityGroup = "contracts.confidentiality";
    public const string RiskLevelGroup = "contracts.risk-level";
    public const string LineKindGroup = "contracts.line-kind";
    public const string PeriodGroup = "contracts.period";
    public const string PartyRoleGroup = "contracts.party-role";

    internal static readonly (string Key, string Label)[] PreparationSource =
        [("Draft", "Taslak"), ("InReview", "İncelemede"), ("ReadyToPublish", "Yayıma hazır")];

    internal static readonly (string Key, string Label)[] ApprovalSource =
    [
        ("NotRequired", "Onay gerekmiyor"), ("Pending", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Rejected", "Onay reddedildi"), ("Withdrawn", "Onay geri çekildi"),
    ];

    internal static readonly (string Key, string Label)[] SignatureSource =
    [
        ("NotSent", "İmzaya gönderilmedi"), ("Pending", "İmza bekliyor"), ("PartiallySigned", "Kısmen imzalandı"), ("Completed", "İmzalandı"),
        ("Rejected", "İmza reddedildi"), ("Expired", "İmza süresi doldu"), ("Cancelled", "İmza iptal edildi"),
    ];

    internal static readonly (string Key, string Label)[] EffectiveSource =
    [
        ("NotInForce", "Yürürlükte değil"), ("AwaitingStart", "Başlangıcı bekleniyor"), ("Active", "Aktif"), ("Suspended", "Askıda"),
        ("Expired", "Süresi doldu"), ("Terminated", "Feshedildi"), ("Cancelled", "İptal edildi"),
    ];

    internal static readonly (string Key, string Label)[] RenewalModeSource =
        [("None", "Yenilemesiz"), ("Manual", "Manuel yenileme"), ("Automatic", "Otomatik yenileme")];

    internal static readonly (string Key, string Label)[] ConfidentialitySource =
        [("Internal", "Kurum içi"), ("Confidential", "Gizli"), ("Restricted", "Kısıtlı")];

    internal static readonly (string Key, string Label)[] RiskLevelSource =
        [("Low", "Düşük"), ("Medium", "Orta"), ("High", "Yüksek")];

    internal static readonly (string Key, string Label)[] LineKindSource =
        [("Service", "Hizmet"), ("Product", "Ürün"), ("Text", "Açıklama satırı")];

    internal static readonly (string Key, string Label)[] PeriodSource =
        [("", "Tek seferlik"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    public static IReadOnlyList<(string Key, string Label)> Preparations => ContractText.Translate(PreparationGroup, PreparationSource);

    public static IReadOnlyList<(string Key, string Label)> Approvals => ContractText.Translate(ApprovalGroup, ApprovalSource);

    public static IReadOnlyList<(string Key, string Label)> Signatures => ContractText.Translate(SignatureGroup, SignatureSource);

    public static IReadOnlyList<(string Key, string Label)> Effectives => ContractText.Translate(EffectiveGroup, EffectiveSource);

    public static IReadOnlyList<(string Key, string Label)> RenewalModes => ContractText.Translate(RenewalModeGroup, RenewalModeSource);

    public static IReadOnlyList<(string Key, string Label)> Confidentialities => ContractText.Translate(ConfidentialityGroup, ConfidentialitySource);

    public static IReadOnlyList<(string Key, string Label)> RiskLevels => ContractText.Translate(RiskLevelGroup, RiskLevelSource);

    public static IReadOnlyList<(string Key, string Label)> LineKinds => ContractText.Translate(LineKindGroup, LineKindSource);

    public static IReadOnlyList<(string Key, string Label)> Periods => ContractText.Translate(PeriodGroup, PeriodSource);

    /// <summary>Taraf rolleri paylaşılan sözleşmededir (<see cref="ContractPartyRoles"/>); Türkçe ad kaynak metin olarak kalır.</summary>
    public static IReadOnlyList<(string Key, string Label)> PartyRoles => ContractText.Translate(PartyRoleGroup, ContractPartyRoles.All);

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
        amount is { } value ? $"{value.ToString("N2", Regional.Numbers)} {currency}" : Texts.Get("contracts.hiddenAmount");

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, CultureInfo.InvariantCulture) : "—";

    /// <summary>Bitişe kalan gün (bugüne göre, tenant saat dilimi); bitişsizde null.</summary>
    public static int? DaysUntil(DateOnly? date) =>
        date is { } d ? d.DayNumber - DateOnly.FromDateTime(Regional.Now.DateTime).DayNumber : null;
}

/// <summary>Eylem ve tarihçe etiketleri (dilim 2); anahtarlar sunucudaki <c>ContractAction</c> / <c>ContractEventKinds</c> ile aynıdır.</summary>
public static class ContractActionLabels
{
    public const string ActionGroup = "contracts.action";
    public const string EventGroup = "contracts.event";
    public const string VersionStatusGroup = "contracts.version-status";
    public const string FieldGroup = "contracts.field";
    public const string ChangeGroup = "contracts.change";

    internal static readonly (string Key, string Label)[] ActionSource =
    [
        ("send-to-review", "İncelemeye gönder"),
        ("mark-ready", "Yayıma hazır işaretle"),
        ("reopen-draft", "Taslağa dön"),
        ("submit-for-approval", "Onaya gönder"),
        ("withdraw-approval", "Onayı geri çek"),
        ("approve", "Onayla"),
        ("reject-approval", "Onayı reddet"),
        ("return-for-changes", "Değişiklik için geri gönder"),
        ("cancel", "Sözleşmeyi iptal et"),
        ("revise", "Revizyon aç"),
    ];

    internal static readonly (string Key, string Label)[] EventSource =
    [
        ("Created", "Oluşturuldu"),
        ("CreatedFromQuote", "Kabul edilmiş tekliften oluşturuldu"),
        ("DocumentGenerated", "Belge üretildi"),
        ("SentToReview", "İncelemeye gönderildi"),
        ("MarkedReady", "Yayıma hazır işaretlendi"),
        ("ReopenedDraft", "Taslağa döndü"),
        ("SubmittedForApproval", "Onaya gönderildi (sürüm donduruldu)"),
        ("ApprovalWithdrawn", "Onay geri çekildi"),
        ("Approved", "Onaylandı"),
        ("ApprovalRejected", "Onay reddedildi"),
        ("ApprovalStepApproved", "Onay adımı onaylandı (sıradaki adım açıldı)"),
        ("ApprovalReturned", "Onaydan değişiklik talebiyle geri gönderildi; yeni revizyon taslağı açıldı"),
        ("ApprovalInvalidated", "Taslağa döndü; önceki onay geçersiz kaldı"),
        ("Activated", "Başlangıç tarihi geldi; yürürlüğe girdi"),
        ("PeriodExpired", "Dönem sona erdi"),
        ("AutoRenewed", "Otomatik uzatıldı"),
        ("RenewalDraftCreated", "Yenileme taslağı açıldı"),
        ("RenewalDeclined", "Yenilememe kararı verildi"),
        ("TerminationScheduled", "Fesih planlandı"),
        ("Terminated", "Feshedildi"),
        ("RenewalReminder", "Yenileme hatırlatması işlendi"),
        ("Cancelled", "İptal edildi"),
        ("Revised", "Yeni revizyon açıldı"),
    ];

    internal static readonly (string Key, string Label)[] VersionStatusSource =
    [
        ("Frozen", "Donduruldu"), ("Approved", "Onaylandı"), ("Rejected", "Reddedildi"), ("Withdrawn", "Geri çekildi"), ("Signed", "İmzalandı"), ("Superseded", "Geçersiz (yeni revizyon)"),
    ];

    internal static readonly (string Key, string Label)[] FieldSource =
    [
        ("title", "Başlık"), ("type", "Tür"), ("description", "Açıklama"), ("partyId", "Müşteri"), ("personId", "İlgili kişi"), ("startDate", "Başlangıç"),
        ("endDate", "Bitiş"), ("isIndefinite", "Belirsiz süreli"), ("currency", "Para birimi"), ("priceIncludesTax", "Vergi dahil"), ("contractValue", "Sözleşme bedeli"),
        ("paymentTerms", "Ödeme koşulları"), ("renewalMode", "Yenileme yöntemi"), ("renewalPeriodMonths", "Yenileme süresi (ay)"), ("noticePeriodDays", "Bildirim süresi (gün)"),
        ("noticeDeadline", "Son bildirim tarihi"), ("confidentiality", "Gizlilik"), ("hasNonStandardTerms", "Standart dışı madde"), ("riskLevel", "Risk düzeyi"), ("netTotal", "Net toplam"),
        ("taxTotal", "Vergi"), ("grossTotal", "Genel toplam"),
        ("kind", "Tür"), ("productCode", "Kod"), ("name", "Ad"), ("quantity", "Miktar"), ("unitName", "Birim"), ("unitPrice", "Birim fiyat"), ("discountPercent", "İndirim %"),
        ("taxRatePercent", "Vergi %"), ("billingPeriod", "Dönem"), ("net", "Net"), ("tax", "Vergi"), ("gross", "Toplam"), ("role", "Rol"), ("notes", "Not"),
    ];

    internal static readonly (string Key, string Label)[] ChangeSource = [("Added", "Eklendi"), ("Removed", "Kaldırıldı"), ("Changed", "Değişti")];

    public static IReadOnlyList<(string Key, string Label)> Actions => ContractText.Translate(ActionGroup, ActionSource);

    public static IReadOnlyList<(string Key, string Label)> Events => ContractText.Translate(EventGroup, EventSource);

    public static IReadOnlyList<(string Key, string Label)> Fields => ContractText.Translate(FieldGroup, FieldSource);

    public static IReadOnlyList<(string Key, string Label)> Changes => ContractText.Translate(ChangeGroup, ChangeSource);

    public static string Action(string key) => ContractText.Lookup(ActionGroup, ActionSource, key);

    public static bool IsPrimary(string key) => key is "submit-for-approval" or "approve" or "mark-ready";

    public static string History(string kind) => ContractText.Lookup(EventGroup, EventSource, kind);

    public static IReadOnlyList<(string Key, string Label)> VersionStatuses => ContractText.Translate(VersionStatusGroup, VersionStatusSource);

    public static string Field(string key) => ContractText.Lookup(FieldGroup, FieldSource, key);

    public static string Change(string key) => ContractText.Lookup(ChangeGroup, ChangeSource, key);
}

/// <summary>Şablon durum ve eylem etiketleri (dilim 4); anahtarlar sunucudaki <c>TemplateStatus</c> ile aynıdır.</summary>
public static class TemplateLabels
{
    public const string StatusGroup = "contracts.template-status";
    public const string ActionGroup = "contracts.template-action";

    internal static readonly (string Key, string Label)[] StatusSource =
        [("Draft", "Taslak"), ("Published", "Yayımlanmış"), ("Archived", "Arşivlenmiş")];

    internal static readonly (string Key, string Label)[] ActionSource =
        [("publish", "Yayımla"), ("archive", "Arşivle"), ("restore", "Geri al")];

    public static IReadOnlyList<(string Key, string Label)> Statuses => ContractText.Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> Actions => ContractText.Translate(ActionGroup, ActionSource);

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string StatusBadge(string status) => status switch { "Published" => "won", "Archived" => "cancelled", _ => "open" };

    public static string Action(string? key) => key is null ? string.Empty : ContractText.Lookup(ActionGroup, ActionSource, key);
}

/// <summary>İmza etiketleri (dilim 6); anahtarlar sunucudaki <c>SignatureRequestStatus</c>, <c>SignatoryStatus</c> ve <c>SignatureAssurance</c> ile aynıdır.</summary>
public static class SignatureLabels
{
    public const string RequestStatusGroup = "contracts.signature-request";
    public const string SignatoryStatusGroup = "contracts.signatory-status";
    public const string AssuranceGroup = "contracts.signature-assurance";
    public const string EventTypeGroup = "contracts.signature-event";
    public const string OutcomeGroup = "contracts.signature-outcome";

    internal static readonly (string Key, string Label)[] RequestStatusSource =
    [
        ("Pending", "İmza bekliyor"), ("PartiallySigned", "Kısmen imzalandı"), ("Completed", "Tamamlandı"), ("Rejected", "Reddedildi"), ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"),
    ];

    internal static readonly (string Key, string Label)[] SignatoryStatusSource =
    [
        ("Waiting", "Sırası gelmedi"), ("Sent", "İmza bekliyor"), ("Viewed", "Görüntüledi"), ("Signed", "İmzaladı"), ("Declined", "Reddetti"), ("Expired", "Süresi doldu"), ("Cancelled", "İptal edildi"),
    ];

    // Güvence düzeyleri birbirine eşit gösterilmez; her biri kendi adıyla ve sınırıyla anılır.
    internal static readonly (string Key, string Label)[] AssuranceSource =
    [
        ("ManualScan", "Islak imza taraması (yetkili doğruladı)"),
        ("SimulatedElectronic", "Simülasyon (hukuki değeri yok)"),
        ("ProviderElectronic", "Sağlayıcı: elektronik imza"),
        ("ProviderQualified", "Sağlayıcı: nitelikli elektronik imza (sağlayıcı bildirimi)"),
    ];

    internal static readonly (string Key, string Label)[] EventTypeSource =
    [
        ("Requested", "İstek açıldı"), ("Sent", "İmzaya sunuldu"), ("Viewed", "Görüntülendi"), ("Signed", "İmzalandı"), ("Declined", "Reddedildi"), ("Reminded", "Hatırlatma kaydedildi"),
        ("Expired", "Süre doldu"), ("Cancelled", "İptal edildi"), ("Completed", "Tamamlandı"),
    ];

    internal static readonly (string Key, string Label)[] OutcomeSource =
        [("Applied", "Uygulandı"), ("Duplicate", "Yinelenen"), ("OutOfOrder", "Sırası bozuk"), ("Rejected", "Reddedildi"), ("Ignored", "Yok sayıldı")];

    public static IReadOnlyList<(string Key, string Label)> RequestStatuses => ContractText.Translate(RequestStatusGroup, RequestStatusSource);

    public static IReadOnlyList<(string Key, string Label)> SignatoryStatuses => ContractText.Translate(SignatoryStatusGroup, SignatoryStatusSource);

    /// <summary>Güvence düzeyleri birbirine eşit gösterilmez; her biri kendi adıyla ve sınırıyla anılır.</summary>
    public static IReadOnlyList<(string Key, string Label)> Assurances => ContractText.Translate(AssuranceGroup, AssuranceSource);

    public static IReadOnlyList<(string Key, string Label)> EventTypes => ContractText.Translate(EventTypeGroup, EventTypeSource);

    public static IReadOnlyList<(string Key, string Label)> Outcomes => ContractText.Translate(OutcomeGroup, OutcomeSource);

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
    public const string ObligorGroup = "contracts.commitment-obligor";
    public const string KindGroup = "contracts.commitment-kind";
    public const string RecurrenceGroup = "contracts.commitment-recurrence";
    public const string DirectionGroup = "contracts.commitment-direction";
    public const string StatusGroup = "contracts.commitment-status";
    public const string OccurrenceStatusGroup = "contracts.occurrence-status";
    public const string FollowUpGroup = "contracts.commitment-followup";

    internal static readonly (string Key, string Label)[] ObligorSource = [("Provider", "Hizmet veren (biz)"), ("Customer", "Müşteri"), ("Both", "Her iki taraf")];

    internal static readonly (string Key, string Label)[] KindSource = [("OneTime", "Tek seferlik"), ("Periodic", "Dönemsel")];

    internal static readonly (string Key, string Label)[] RecurrenceSource = [("Weekly", "Haftalık"), ("Monthly", "Aylık"), ("Quarterly", "Üç aylık"), ("Yearly", "Yıllık")];

    internal static readonly (string Key, string Label)[] DirectionSource = [("AtLeast", "En az"), ("AtMost", "En çok"), ("Exactly", "Tam (toleransla)")];

    internal static readonly (string Key, string Label)[] StatusSource = [("Active", "Etkin"), ("Completed", "Tamamlandı"), ("Cancelled", "İptal edildi")];

    internal static readonly (string Key, string Label)[] OccurrenceStatusSource =
        [("Open", "Açık"), ("Fulfilled", "Yerine getirildi"), ("Breached", "İhlal"), ("Waived", "Muaf"), ("Cancelled", "İptal edildi")];

    internal static readonly (string Key, string Label)[] FollowUpSource =
        [("None", "Takip aktivitesi yok"), ("Created", "Takip aktivitesi açıldı"), ("Unavailable", "Aktivite modülü kapalı"), ("Failed", "Takip aktivitesi açılamadı (yeniden denenecek)")];

    public static IReadOnlyList<(string Key, string Label)> Obligors => ContractText.Translate(ObligorGroup, ObligorSource);

    public static IReadOnlyList<(string Key, string Label)> Kinds => ContractText.Translate(KindGroup, KindSource);

    public static IReadOnlyList<(string Key, string Label)> Recurrences => ContractText.Translate(RecurrenceGroup, RecurrenceSource);

    public static IReadOnlyList<(string Key, string Label)> Directions => ContractText.Translate(DirectionGroup, DirectionSource);

    public static IReadOnlyList<(string Key, string Label)> Statuses => ContractText.Translate(StatusGroup, StatusSource);

    public static IReadOnlyList<(string Key, string Label)> OccurrenceStatuses => ContractText.Translate(OccurrenceStatusGroup, OccurrenceStatusSource);

    public static IReadOnlyList<(string Key, string Label)> FollowUps => ContractText.Translate(FollowUpGroup, FollowUpSource);

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

/// <summary>Yenileme, fesih ve hatırlatma etiketleri (dilim 8); anahtarlar sunucudaki <c>RenewalOutcome</c>, <c>NonRenewalReason</c>, <c>ReminderKind</c> ve <c>ReminderStatus</c> ile aynıdır.</summary>
public static class RenewalLabels
{
    public const string OutcomeGroup = "contracts.renewal-outcome";
    public const string ReasonGroup = "contracts.renewal-reason";
    public const string SourceGroup = "contracts.renewal-source";
    public const string ReminderKindGroup = "contracts.reminder-kind";
    public const string ReminderStatusGroup = "contracts.reminder-status";
    public const string RelationGroup = "contracts.renewal-relation";

    internal static readonly (string Key, string Label)[] OutcomeSource =
        [("DraftCreated", "Yenileme taslağı açıldı"), ("AutoExtended", "Otomatik uzatıldı"), ("NotRenewed", "Yenilenmeyecek")];

    internal static readonly (string Key, string Label)[] ReasonSource =
    [
        ("Price", "Fiyat"), ("Competitor", "Rakibe geçti"), ("NoLongerNeeded", "Artık ihtiyaç yok"), ("ServiceQuality", "Hizmet kalitesi"), ("CustomerClosed", "Müşteri faaliyeti sonlandırdı"), ("Other", "Diğer"),
    ];

    internal static readonly (string Key, string Label)[] SourceSource = [("Scheduler", "Zamanlanmış iş"), ("User", "Kullanıcı")];

    internal static readonly (string Key, string Label)[] ReminderKindSource = [("Renewal", "Yenileme hatırlatması"), ("Escalation", "Eskalasyon"), ("Approval", "Onay hatırlatması")];

    internal static readonly (string Key, string Label)[] ReminderStatusSource =
        [("Created", "Aktivite açıldı"), ("Skipped", "Atlandı"), ("Unavailable", "Aktivite modülü kapalı"), ("Failed", "Açılamadı (yeniden denenecek)")];

    internal static readonly (string Key, string Label)[] RelationSource = [("Predecessor", "Önceki dönem"), ("Current", "Bu sözleşme"), ("Successor", "Yenileme")];

    public static IReadOnlyList<(string Key, string Label)> Outcomes => ContractText.Translate(OutcomeGroup, OutcomeSource);

    public static IReadOnlyList<(string Key, string Label)> Reasons => ContractText.Translate(ReasonGroup, ReasonSource);

    public static IReadOnlyList<(string Key, string Label)> Sources => ContractText.Translate(SourceGroup, SourceSource);

    public static IReadOnlyList<(string Key, string Label)> ReminderKinds => ContractText.Translate(ReminderKindGroup, ReminderKindSource);

    public static IReadOnlyList<(string Key, string Label)> ReminderStatuses => ContractText.Translate(ReminderStatusGroup, ReminderStatusSource);

    public static IReadOnlyList<(string Key, string Label)> Relations => ContractText.Translate(RelationGroup, RelationSource);

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string Badge(string? decision) => decision switch
    {
        "DraftCreated" or "AutoExtended" => "won",
        "NotRenewed" => "lost",
        null => "onhold",
        _ => "open",
    };

    /// <summary>Son bildirim ya da bitişe kalan gün için kısa açıklama.</summary>
    public static string Days(int? days) => days switch
    {
        null => "—",
        0 => Texts.Get("contracts.days.today"),
        > 0 => Texts.Get("contracts.days.left", days),
        _ => Texts.Get("contracts.days.past", -days),
    };
}

/// <summary>Liste görünümü (dilim 9): hızlı görünümler, sıralama seçenekleri ve pano etiketleri; anahtarlar sunucudaki <c>ContractViewRules</c>, <c>ContractSort</c> ve <c>ContractDashboardService</c> ile aynıdır.</summary>
public static class ViewLabels
{
    public const string PresetGroup = "contracts.view-preset";
    public const string SortGroup = "contracts.sort";
    public const string CounterGroup = "contracts.counter";

    internal static readonly (string Key, string Label)[] PresetSource =
    [
        ("mine", "Sözleşmelerim"), ("signature", "İmza bekleyen"), ("ending", "Bitişi yaklaşan (90 gün)"), ("notice", "Bildirim süresi yaklaşan (30 gün)"), ("overdue", "Taahhüdü geciken"), ("sales", "Satış sözleşmeleri"),
    ];

    internal static readonly (string Key, string Label)[] SortSource =
    [
        ("-createdAt", "En yeni önce"), ("createdAt", "En eski önce"), ("title", "Başlık A→Z"), ("-title", "Başlık Z→A"), ("number", "Numara (artan)"), ("-number", "Numara (azalan)"),
        ("endDate", "Bitiş: yakın önce"), ("-endDate", "Bitiş: uzak önce"), ("noticeDeadline", "Son bildirim: yakın önce"), ("startDate", "Başlangıç: eski önce"), ("-startDate", "Başlangıç: yeni önce"),
    ];

    internal static readonly (string Key, string Label)[] CounterSource =
    [
        ("total", "Tüm sözleşmeler"), ("drafts", "Hazırlıkta (taslak)"), ("approval", "Onay bekleyen"), ("signature", "İmza bekleyen"), ("active", "Yürürlükte"),
        ("awaitingStart", "Başlangıcı bekleyen"), ("ending", "Bitişi yaklaşan (90 gün)"), ("notice", "Bildirim süresi yaklaşan (30 gün)"), ("overdue", "Taahhüdü geciken"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Presets => ContractText.Translate(PresetGroup, PresetSource);

    public static IReadOnlyList<(string Key, string Label)> Sorts => ContractText.Translate(SortGroup, SortSource);

    public static IReadOnlyList<(string Key, string Label)> Counters => ContractText.Translate(CounterGroup, CounterSource);

    /// <summary>Panodaki sayaç tıklanınca açılan liste adresi (hızlı görünüm ya da süzgeç).</summary>
    public static string CounterLink(string key) => key switch
    {
        "signature" or "ending" or "notice" or "overdue" => $"sozlesmeler?gorunum={key}",
        "approval" => "sozlesmeler?onay=Pending",
        "drafts" => "sozlesmeler?hazirlik=Draft",
        "active" => "sozlesmeler?yururluk=Active",
        "awaitingStart" => "sozlesmeler?yururluk=AwaitingStart",
        _ => "sozlesmeler",
    };

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) => list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";
}
