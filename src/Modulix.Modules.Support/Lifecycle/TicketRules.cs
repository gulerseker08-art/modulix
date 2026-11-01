using System.Security.Cryptography;

namespace Modulix.Modules.Support.Lifecycle;

// Talep durum sabitleri ve saf kurallar (ADR-0024). Kurallar sunucuda uygulanır; arayüz yalnızca aynı kuralları gösterir.

/// <summary>
/// Talep durumu. Çözüldü ile Kapandı aynı anlama gelmez: çözüldü, çözümün kaydedildiği ve müşteri teyidi/itirazının beklendiği durumdur;
/// kapandı ise normal yolla yeniden açılamayan son durumdur (yeniden açma penceresi içinde yetkiyle açılır).
/// </summary>
public static class TicketStatus
{
    public const string New = "New";
    public const string Triage = "Triage";
    public const string Assigned = "Assigned";
    public const string InProgress = "InProgress";
    public const string WaitingCustomer = "WaitingCustomer";
    public const string WaitingInternal = "WaitingInternal";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
    public const string Reopened = "Reopened";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> All =
        [New, Triage, Assigned, InProgress, WaitingCustomer, WaitingInternal, Resolved, Closed, Reopened, Cancelled];

    /// <summary>Son durumlar: talep üzerinde yeni iş yapılmaz (kapalı talep yalnızca pencere içinde yeniden açılır).</summary>
    public static bool IsFinal(string status) => status is Closed or Cancelled;

    /// <summary>Çalışılan (çözülmemiş, kapanmamış) durumlar: müşteri yanıtı, atama ve devir bu durumlarda yapılır.</summary>
    public static bool IsWorking(string status) => status is New or Triage or Assigned or InProgress or WaitingCustomer or WaitingInternal or Reopened;

    /// <summary>İş kuyruğunda "açık" sayılan durumlar (çözüldü müşteri teyidini beklerken de açıktır).</summary>
    public static bool IsOpen(string status) => !IsFinal(status);
}

public static class TicketChannels
{
    public const string User = "User";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Web = "Web";
    public const string Portal = "Portal";
    public const string Api = "Api";

    public static readonly IReadOnlyList<string> All = [User, Phone, Email, Web, Portal, Api];
}

/// <summary>Etki ve aciliyet düzeyi.</summary>
public static class TicketLevel
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static readonly IReadOnlyList<string> All = [Low, Medium, High];
}

public static class TicketPriority
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Critical = "Critical";

    public static readonly IReadOnlyList<string> All = [Low, Normal, High, Critical];
}

public static class MessageVisibility
{
    /// <summary>Müşteriye görünen yanıt ya da müşteriden gelen mesaj.</summary>
    public const string Public = "Public";

    /// <summary>İç not: müşteriye hiçbir yoldan gitmez.</summary>
    public const string Internal = "Internal";
}

public static class MessageDirection
{
    /// <summary>Destek ekibinden müşteriye.</summary>
    public const string Outbound = "Outbound";

    /// <summary>Müşteriden gelen mesaj (telefon/e-posta/portal; temsilci kaydeder ya da kanal adaptörü yazar).</summary>
    public const string Inbound = "Inbound";

    /// <summary>İç not.</summary>
    public const string Note = "Note";
}

/// <summary>
/// Dış mesajın gönderim durumu. Kuyruğa alınan ile müşteriye ulaşan aynı şey değildir: gerçek teslim/başarısızlık bilgisi kanal adaptörünün
/// işidir (dilim 4). Başarısız gönderim ilk yanıt sayılmaz.
/// </summary>
public static class DeliveryStatus
{
    public const string NotRequired = "NotRequired";
    public const string Recorded = "Recorded";
    public const string Queued = "Queued";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public static class TicketEventKinds
{
    public const string Created = "Created";
    public const string Triaged = "Triaged";
    public const string Assigned = "Assigned";
    public const string Taken = "Taken";
    public const string Transferred = "Transferred";
    public const string Started = "Started";
    public const string WaitingCustomer = "WaitingCustomer";
    public const string WaitingInternal = "WaitingInternal";
    public const string Resumed = "Resumed";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
    public const string Reopened = "Reopened";
    public const string Cancelled = "Cancelled";
    public const string PriorityChanged = "PriorityChanged";
    public const string CustomerLinked = "CustomerLinked";
    public const string PublicReply = "PublicReply";
    public const string CustomerMessage = "CustomerMessage";
    public const string InternalNote = "InternalNote";
}

/// <summary>İş akışı eylemleri; adlar API yoludur (<c>POST /api/support/tickets/{id}/{eylem}</c>).</summary>
public static class TicketAction
{
    public const string Triage = "triage";
    public const string Assign = "assign";
    public const string Take = "take";
    public const string Transfer = "transfer";
    public const string Start = "start";
    public const string WaitCustomer = "wait-customer";
    public const string WaitInternal = "wait-internal";
    public const string Resume = "resume";
    public const string Resolve = "resolve";
    public const string Close = "close";
    public const string Reopen = "reopen";
    public const string Cancel = "cancel";
    public const string SetPriority = "set-priority";

    public static readonly IReadOnlyList<string> All =
        [Triage, Assign, Take, Transfer, Start, WaitCustomer, WaitInternal, Resume, Resolve, Close, Reopen, Cancel, SetPriority];
}

/// <summary>Tenant ayarlarından gelen talep politikası; sağlayıcı yoksa varsayılan.</summary>
public sealed record TicketPolicySettings(int ReopenWindowDays = 7, bool RequireRootCauseForComplaint = true)
{
    public static TicketPolicySettings Default { get; } = new();
}

/// <summary>Geçiş kuralları için talebin durum özeti.</summary>
public readonly record struct TicketState(
    string Status, bool HasOwner, DateTimeOffset? ClosedAt, DateTimeOffset Now, int ReopenWindowDays);

/// <summary>Bir kuralın sonucu: yapılabilir mi, yapılamıyorsa kod ve açıklama.</summary>
public sealed record TicketRuleResult(bool Allowed, string? Code, string? Message)
{
    public static TicketRuleResult Ok { get; } = new(true, null, null);

    public static TicketRuleResult Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// Geçiş tablosu (saf kural; veri erişimi yok). Sunucu her eylemde bunu çağırır; arayüz aynı sonucu düğme durumu ve açıklama olarak kullanır.
/// <list type="table">
///   <item>Yeni → Sınıflandırma: <c>triage</c></item>
///   <item>Yeni/Sınıflandırma/Yeniden açıldı → Atandı: <c>assign</c> (yetkili) ya da <c>take</c> (üzerine alma); diğer çalışılan durumda durum değişmez</item>
///   <item>Herhangi çalışılan durum → aynı durum, başka kuyruk: <c>transfer</c> (gerekçeli)</item>
///   <item>Yeni/Sınıflandırma/Atandı/Yeniden açıldı → İşlemde: <c>start</c> (sorumlu atanmış olmalı)</item>
///   <item>Atandı/İşlemde/bekleme → Müşteriden bilgi bekleniyor ya da İç ekip bekleniyor: <c>wait-customer</c> / <c>wait-internal</c> (gerekçeli)</item>
///   <item>Bekleme → İşlemde: <c>resume</c></item>
///   <item>Atandı/İşlemde/bekleme/Yeniden açıldı → Çözüldü: <c>resolve</c> (çözüm alanları zorunlu)</item>
///   <item>Çözüldü → Kapandı: <c>close</c></item>
///   <item>Çözüldü ya da (pencere içinde) Kapandı → Yeniden açıldı: <c>reopen</c> (gerekçeli)</item>
///   <item>Çalışılan durum → İptal edildi: <c>cancel</c> (gerekçeli)</item>
///   <item>Son olmayan her durum: <c>set-priority</c> (ayrı yetki, gerekçeli)</item>
/// </list>
/// </summary>
public static class TicketTransitions
{
    public static TicketRuleResult Can(string action, TicketState s)
    {
        switch (action)
        {
            case TicketAction.Triage:
                return s.Status == TicketStatus.New ? TicketRuleResult.Ok : TicketRuleResult.Deny("not-new", "Yalnızca yeni talep sınıflandırmaya alınabilir.");

            case TicketAction.Assign:
            case TicketAction.Take:
                return TicketStatus.IsWorking(s.Status)
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("not-working", "Çözülmüş, kapanmış ya da iptal edilmiş talebe atama yapılamaz; önce yeniden açın.");

            case TicketAction.Transfer:
                return TicketStatus.IsWorking(s.Status)
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("not-working", "Yalnızca çalışılan talep başka kuyruğa devredilebilir.");

            case TicketAction.Start:
                if (s.Status is not (TicketStatus.New or TicketStatus.Triage or TicketStatus.Assigned or TicketStatus.Reopened))
                {
                    return TicketRuleResult.Deny("cannot-start", "Talep bu durumdan işleme alınamaz.");
                }

                return s.HasOwner ? TicketRuleResult.Ok : TicketRuleResult.Deny("no-owner", "İşleme almak için önce talebe bir sorumlu atanmalıdır.");

            case TicketAction.WaitCustomer:
            case TicketAction.WaitInternal:
            {
                var target = TargetStatus(action, s.Status);
                if (s.Status is not (TicketStatus.Assigned or TicketStatus.InProgress or TicketStatus.WaitingCustomer or TicketStatus.WaitingInternal))
                {
                    return TicketRuleResult.Deny("cannot-wait", "Yalnızca atanmış ya da işlemdeki talep bekletilebilir.");
                }

                return s.Status == target ? TicketRuleResult.Deny("same-status", "Talep zaten bu bekleme durumunda.") : TicketRuleResult.Ok;
            }

            case TicketAction.Resume:
                return s.Status is TicketStatus.WaitingCustomer or TicketStatus.WaitingInternal
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("not-waiting", "Yalnızca beklemedeki talep sürdürülebilir.");

            case TicketAction.Resolve:
                return s.Status is TicketStatus.Assigned or TicketStatus.InProgress or TicketStatus.WaitingCustomer or TicketStatus.WaitingInternal or TicketStatus.Reopened
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("cannot-resolve", "Talep bu durumdan çözülemez; önce sorumlu atayıp işleme alın.");

            case TicketAction.Close:
                return s.Status == TicketStatus.Resolved
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("not-resolved", "Yalnızca çözülmüş talep kapatılabilir.");

            case TicketAction.Reopen:
                if (s.Status == TicketStatus.Resolved)
                {
                    return TicketRuleResult.Ok;
                }

                if (s.Status != TicketStatus.Closed)
                {
                    return TicketRuleResult.Deny("not-closed", "Yalnızca çözülmüş ya da kapanmış talep yeniden açılabilir.");
                }

                return s.ClosedAt is { } closedAt && s.Now - closedAt <= TimeSpan.FromDays(s.ReopenWindowDays)
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("reopen-window", $"Kapanıştan sonra {s.ReopenWindowDays} gün geçtiği için talep yeniden açılamaz; ilişkili yeni talep açın.");

            case TicketAction.Cancel:
                return TicketStatus.IsWorking(s.Status)
                    ? TicketRuleResult.Ok
                    : TicketRuleResult.Deny("not-working", "Yalnızca çalışılan talep iptal edilebilir; çözülmüş talep için yeniden açma ya da kapatma kullanın.");

            case TicketAction.SetPriority:
                return TicketStatus.IsFinal(s.Status)
                    ? TicketRuleResult.Deny("final", "Kapanmış ya da iptal edilmiş talebin önceliği değiştirilemez.")
                    : TicketRuleResult.Ok;

            default:
                return TicketRuleResult.Deny("unknown", "Bilinmeyen eylem.");
        }
    }

    /// <summary>Gerekçe zorunlu eylemler (devir, bekletme, yeniden açma, iptal, elle öncelik).</summary>
    public static bool RequiresReason(string action) =>
        action is TicketAction.Transfer or TicketAction.WaitCustomer or TicketAction.WaitInternal or TicketAction.Reopen or TicketAction.Cancel or TicketAction.SetPriority;

    /// <summary>Eylem sonucu durum; durumu değiştirmeyen eylemlerde mevcut durum döner.</summary>
    public static string TargetStatus(string action, string current) => action switch
    {
        TicketAction.Triage => TicketStatus.Triage,
        TicketAction.Assign or TicketAction.Take =>
            current is TicketStatus.New or TicketStatus.Triage or TicketStatus.Reopened ? TicketStatus.Assigned : current,
        TicketAction.Start or TicketAction.Resume => TicketStatus.InProgress,
        TicketAction.WaitCustomer => TicketStatus.WaitingCustomer,
        TicketAction.WaitInternal => TicketStatus.WaitingInternal,
        TicketAction.Resolve => TicketStatus.Resolved,
        TicketAction.Close => TicketStatus.Closed,
        TicketAction.Reopen => TicketStatus.Reopened,
        TicketAction.Cancel => TicketStatus.Cancelled,
        _ => current,
    };
}

/// <summary>Etki × aciliyet → öncelik matrisi (dilim 1: sabit varsayılan; yönetilebilir matris dilim 2).</summary>
public static class PriorityMatrix
{
    public static string Compute(string impact, string urgency) => (impact, urgency) switch
    {
        (TicketLevel.High, TicketLevel.High) => TicketPriority.Critical,
        (TicketLevel.High, TicketLevel.Medium) or (TicketLevel.Medium, TicketLevel.High) => TicketPriority.High,
        (TicketLevel.High, TicketLevel.Low) or (TicketLevel.Medium, TicketLevel.Medium) or (TicketLevel.Low, TicketLevel.High) => TicketPriority.Normal,
        _ => TicketPriority.Low,
    };
}

/// <summary>Çözüm alanı kuralları: çözümde açıklama ve çözüm kodu zorunludur; şikâyette kök neden ve düzeltici aksiyon da (politikaya göre).</summary>
public static class ResolutionRules
{
    public static IReadOnlyList<(string Field, string Message)> Validate(
        string? resolutionCode, string? resolutionNote, string? rootCause, string? correctiveAction, bool isComplaint, TicketPolicySettings policy)
    {
        var errors = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(resolutionCode))
        {
            errors.Add(("resolutionCode", "Çözüm kodu zorunludur."));
        }

        if (string.IsNullOrWhiteSpace(resolutionNote))
        {
            errors.Add(("resolutionNote", "Çözüm açıklaması zorunludur."));
        }

        if (isComplaint && policy.RequireRootCauseForComplaint)
        {
            if (string.IsNullOrWhiteSpace(rootCause))
            {
                errors.Add(("rootCause", "Şikâyetin çözümünde kök neden zorunludur."));
            }

            if (string.IsNullOrWhiteSpace(correctiveAction))
            {
                errors.Add(("correctiveAction", "Şikâyetin çözümünde düzeltici aksiyon zorunludur."));
            }
        }

        return errors;
    }
}

/// <summary>
/// İlk insan yanıtı kuralı: müşteriye giden (Public, Outbound), otomatik olmayan ve gönderimi kabul edilmiş (başarısız ya da taslak olmayan)
/// ilk mesaj sayılır. Otomatik alındı mesajı, iç not ve müşteriden gelen mesaj sayılmaz.
/// </summary>
public static class FirstResponseRules
{
    public static bool Counts(string visibility, string direction, bool isAutomatic, string deliveryStatus) =>
        visibility == MessageVisibility.Public
        && direction == MessageDirection.Outbound
        && !isAutomatic
        && deliveryStatus is DeliveryStatus.Queued or DeliveryStatus.Sent or DeliveryStatus.Recorded;
}

public static class TicketNumbers
{
    /// <summary>Yeni talep numarası: TK-yyyyMMdd-XXXXXX (karışmayan harf/rakam alfabesi).</summary>
    public static string New(DateTimeOffset createdAt)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return $"TK-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }
}
