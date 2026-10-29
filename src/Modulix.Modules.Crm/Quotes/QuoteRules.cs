namespace Modulix.Modules.Crm.Quotes;

/// <summary>Teklifin ticari durumları. İç onay ve gönderim durumundan bağımsızdır.</summary>
public static class QuoteStatus
{
    public const string Draft = "Draft";
    public const string Presented = "Presented";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";

    /// <summary>Aynı ailede yeni revizyon açılınca ya da yeni sürüm sunulunca geçersizleşen sürüm.</summary>
    public const string Superseded = "Superseded";

    public static readonly IReadOnlyList<string> All = [Draft, Presented, Accepted, Rejected, Expired, Cancelled, Superseded];

    /// <summary>Artık hiçbir geçiş yapılamayan durumlar.</summary>
    public static bool IsFinal(string status) => status is Accepted or Rejected or Expired or Cancelled or Superseded;
}

/// <summary>İç onay durumu (ticari durumdan ayrı).</summary>
public static class QuoteApprovalStatus
{
    public const string NotRequired = "NotRequired";
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string ChangesRequested = "ChangesRequested";

    public static readonly IReadOnlyList<string> All = [NotRequired, Pending, Approved, Rejected, ChangesRequested];
}

/// <summary>
/// Gönderim süreci. Sağlayıcının kabul etmesi (<see cref="Succeeded"/>) müşteriye teslim edildiği anlamına gelmez; teslim bilgisi
/// ayrı bir kanıt olmadan "teslim edildi" diye gösterilmez.
/// </summary>
public static class QuoteDeliveryStatus
{
    public const string NotSent = "NotSent";
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";

    public static readonly IReadOnlyList<string> All = [NotSent, Pending, Processing, Succeeded, Failed];
}

public static class QuoteAction
{
    public const string SubmitForApproval = "submit-for-approval";
    public const string WithdrawApproval = "withdraw-approval";
    public const string Approve = "approve";
    public const string RejectApproval = "reject-approval";
    public const string RequestChanges = "request-changes";
    public const string Present = "present";
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Cancel = "cancel";
}

/// <summary>Bir eylemin neden yapılamadığını açıklayan sonuç (kullanıcıya gösterilir).</summary>
public readonly record struct QuoteRuleResult(bool Allowed, string? Code = null, string? Message = null)
{
    public static QuoteRuleResult Ok { get; } = new(true);

    public static QuoteRuleResult Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// Teklif geçiş kuralları (saf; veri erişimi yok). Sunucu her eylemde bunu çağırır; arayüz aynı sonucu düğme durumu ve
/// açıklama olarak kullanır. Süre kontrolü kabul anında da yapılır, zamanlanmış işe bırakılmaz.
/// </summary>
public static class QuoteRules
{
    public static QuoteRuleResult Can(string action, string status, string approval, DateTimeOffset? validUntil, DateTimeOffset now, bool hasLines)
    {
        bool expired = validUntil is { } until && until <= now;
        switch (action)
        {
            case QuoteAction.SubmitForApproval:
                if (status != QuoteStatus.Draft)
                {
                    return QuoteRuleResult.Deny("not-draft", "Yalnızca taslak teklif onaya gönderilebilir.");
                }

                if (!hasLines)
                {
                    return QuoteRuleResult.Deny("no-lines", "Onaya göndermek için en az bir kalem gerekir.");
                }

                return approval is QuoteApprovalStatus.Pending
                    ? QuoteRuleResult.Deny("already-pending", "Teklif zaten onay bekliyor.")
                    : QuoteRuleResult.Ok;

            case QuoteAction.WithdrawApproval:
                return status == QuoteStatus.Draft && approval == QuoteApprovalStatus.Pending
                    ? QuoteRuleResult.Ok
                    : QuoteRuleResult.Deny("not-pending", "Geri çekilecek bekleyen onay yok.");

            case QuoteAction.Approve or QuoteAction.RejectApproval or QuoteAction.RequestChanges:
                return status == QuoteStatus.Draft && approval == QuoteApprovalStatus.Pending
                    ? QuoteRuleResult.Ok
                    : QuoteRuleResult.Deny("not-pending", "Teklif onay beklemiyor.");

            case QuoteAction.Present:
                if (status != QuoteStatus.Draft)
                {
                    return QuoteRuleResult.Deny("not-draft", "Yalnızca taslak teklif müşteriye sunulabilir.");
                }

                if (!hasLines)
                {
                    return QuoteRuleResult.Deny("no-lines", "Sunmak için en az bir kalem gerekir.");
                }

                if (expired)
                {
                    return QuoteRuleResult.Deny("expired", "Geçerlilik tarihi geçmiş teklif sunulamaz; tarihi güncelleyin.");
                }

                // Onay gerekiyorsa tamamlanmadan sunum sunucuda engellenir.
                return approval is QuoteApprovalStatus.NotRequired or QuoteApprovalStatus.Approved
                    ? QuoteRuleResult.Ok
                    : QuoteRuleResult.Deny("approval-required", "İç onay tamamlanmadan teklif müşteriye sunulamaz.");

            case QuoteAction.Accept or QuoteAction.Reject:
                if (status != QuoteStatus.Presented)
                {
                    return QuoteRuleResult.Deny(StatusCode(status), StatusMessage(status));
                }

                return expired
                    ? QuoteRuleResult.Deny("expired", "Teklifin geçerlilik süresi dolmuş.")
                    : QuoteRuleResult.Ok;

            case QuoteAction.Cancel:
                return status is QuoteStatus.Draft or QuoteStatus.Presented
                    ? QuoteRuleResult.Ok
                    : QuoteRuleResult.Deny(StatusCode(status), "Bu durumdaki teklif iptal edilemez.");

            default:
                return QuoteRuleResult.Deny("unknown-action", "Bilinmeyen eylem.");
        }
    }

    /// <summary>Müşteriye sunulmuş ya da sonuçlanmış sürümün ticari içeriği yerinde değiştirilemez.</summary>
    public static bool IsCommerciallyEditable(string status) => status == QuoteStatus.Draft;

    private static string StatusCode(string status) => status switch
    {
        QuoteStatus.Accepted => "already-accepted",
        QuoteStatus.Rejected => "already-rejected",
        QuoteStatus.Cancelled => "cancelled",
        QuoteStatus.Superseded => "superseded",
        QuoteStatus.Expired => "expired",
        _ => "not-presented",
    };

    private static string StatusMessage(string status) => status switch
    {
        QuoteStatus.Accepted => "Teklif daha önce kabul edilmiş.",
        QuoteStatus.Rejected => "Teklif daha önce reddedilmiş.",
        QuoteStatus.Cancelled => "Teklif iptal edilmiş.",
        QuoteStatus.Superseded => "Bu sürüm yeni bir revizyonla geçersizleşmiş.",
        QuoteStatus.Expired => "Teklifin geçerlilik süresi dolmuş.",
        _ => "Teklif henüz müşteriye sunulmamış.",
    };
}
