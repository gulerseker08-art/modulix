namespace Modulix.Modules.Orders.Lifecycle;

/// <summary>Ticari durum eylemleri; adlar API yoludur (<c>POST /api/orders/orders/{id}/{eylem}</c>).</summary>
public static class OrderAction
{
    public const string SubmitForApproval = "submit-for-approval";
    public const string WithdrawApproval = "withdraw-approval";
    public const string Approve = "approve";
    public const string ReturnToDraft = "return-to-draft";
    public const string Cancel = "cancel";
    public const string Close = "close";
    public const string ForceClose = "force-close";

    public static readonly IReadOnlyList<string> All = [SubmitForApproval, WithdrawApproval, Approve, ReturnToDraft, Cancel, Close, ForceClose];

    /// <summary>Onaylama kararı (onaylayan yetkisi ve dört göz ilkesi uygulanır).</summary>
    public static bool IsDecision(string action) => action is Approve or ReturnToDraft;
}

public static class ChangeRequestStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Withdrawn = "Withdrawn";

    public static readonly IReadOnlyList<string> All = [Pending, Approved, Rejected, Withdrawn];
}

/// <summary>Geçiş kuralları için siparişin durum özeti.</summary>
public readonly record struct OrderState(string Status, string HandoverStatus, string FulfillmentStatus, int ActiveLines);

/// <summary>
/// Sipariş geçiş tablosu (saf kural; veri erişimi yok). Sunucu her eylemde bunu çağırır; arayüz aynı sonucu düğme durumu ve açıklama
/// olarak kullanır.
/// <list type="table">
///   <item>Taslak → Onay bekliyor: <c>submit-for-approval</c> (en az bir etkin kalem)</item>
///   <item>Onay bekliyor → Taslak: <c>withdraw-approval</c> (gönderen, gerekçeli) ya da <c>return-to-draft</c> (onaylayan, gerekçeli)</item>
///   <item>Onay bekliyor → Onaylandı: <c>approve</c> (onaylayan yetkisi; gönderen kendi siparişini onaylayamaz, ayarla izin verilebilir)</item>
///   <item>Taslak/Onay bekliyor/Onaylandı → İptal edildi: <c>cancel</c> (gerekçeli; teslim edilen miktar korunur)</item>
///   <item>Onaylandı → Kapatıldı: <c>close</c> (gerçekleşme tamamlanmış olmalı) ya da <c>force-close</c> (kalan yükümlülük varken, ayrı yetki ve gerekçe)</item>
/// </list>
/// </summary>
public static class OrderTransitions
{
    public static OrderRuleResult Can(string action, OrderState s)
    {
        switch (action)
        {
            case OrderAction.SubmitForApproval:
                if (s.Status != OrderStatus.Draft)
                {
                    return OrderRuleResult.Deny("not-draft", "Yalnızca taslak sipariş onaya gönderilebilir.");
                }

                return s.ActiveLines == 0 ? OrderRuleResult.Deny("no-lines", "Onaya göndermek için en az bir etkin kalem gerekir.") : OrderRuleResult.Ok;

            case OrderAction.WithdrawApproval:
            case OrderAction.ReturnToDraft:
            case OrderAction.Approve:
                return s.Status == OrderStatus.PendingApproval
                    ? OrderRuleResult.Ok
                    : OrderRuleResult.Deny("not-pending", "Yalnızca onay bekleyen sipariş için yapılabilir.");

            case OrderAction.Cancel:
                return s.Status is OrderStatus.Draft or OrderStatus.PendingApproval or OrderStatus.Approved
                    ? OrderRuleResult.Ok
                    : OrderRuleResult.Deny("final", s.Status == OrderStatus.Cancelled ? "Sipariş zaten iptal edilmiş." : "Kapatılmış sipariş iptal edilemez.");

            case OrderAction.Close:
                if (s.Status != OrderStatus.Approved)
                {
                    return OrderRuleResult.Deny("not-approved", "Yalnızca onaylanmış sipariş kapatılabilir.");
                }

                return s.FulfillmentStatus == FulfillmentStatus.Completed
                    ? OrderRuleResult.Ok
                    : OrderRuleResult.Deny("obligation-remaining", "Gerçekleşme tamamlanmadığı için sipariş kapatılamaz; kalan yükümlülük var. Yetkiniz varsa gerekçeyle zorla kapatabilirsiniz.");

            case OrderAction.ForceClose:
                if (s.Status != OrderStatus.Approved)
                {
                    return OrderRuleResult.Deny("not-approved", "Yalnızca onaylanmış sipariş kapatılabilir.");
                }

                return s.FulfillmentStatus == FulfillmentStatus.Completed
                    ? OrderRuleResult.Deny("nothing-remaining", "Gerçekleşme tamamlandı; zorla kapatma gerekmez, normal kapatın.")
                    : OrderRuleResult.Ok;

            default:
                return OrderRuleResult.Deny("unknown", "Bilinmeyen eylem.");
        }
    }

    /// <summary>Gerekçe zorunlu eylemler (geri alma, reddetme benzeri, iptal ve zorla kapatma).</summary>
    public static bool RequiresReason(string action) => action is OrderAction.WithdrawApproval or OrderAction.ReturnToDraft or OrderAction.Cancel or OrderAction.ForceClose;

    /// <summary>Eylem sonucu ticari durum.</summary>
    public static string TargetStatus(string action) => action switch
    {
        OrderAction.SubmitForApproval => OrderStatus.PendingApproval,
        OrderAction.WithdrawApproval or OrderAction.ReturnToDraft => OrderStatus.Draft,
        OrderAction.Approve => OrderStatus.Approved,
        OrderAction.Cancel => OrderStatus.Cancelled,
        OrderAction.Close or OrderAction.ForceClose => OrderStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
