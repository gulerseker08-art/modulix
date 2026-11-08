namespace Modulix.Web.Services;

/// <summary>Onay talebi durumlarının Türkçe adları ve rozet tonları (ADR-0027 Dilim 4). Durum değerleri sunucunun <c>ApprovalRequestStatus</c> adlarıdır.</summary>
public static class ApprovalLabels
{
    public static string StatusText(string status) => status switch
    {
        "Pending" => "Bekliyor",
        "Approved" => "Onaylandı",
        "Rejected" => "Reddedildi",
        "Withdrawn" => "Geri çekildi",
        "Invalidated" => "Geçersizleşti",
        _ => status,
    };

    public static string StatusClass(string status) => status switch
    {
        "Pending" => "stage-open",
        "Approved" => "stage-won",
        "Rejected" => "stage-lost",
        "Invalidated" => "stage-onhold",
        _ => "stage-cancelled",
    };
}
