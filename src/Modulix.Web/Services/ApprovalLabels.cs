namespace Modulix.Web.Services;

/// <summary>
/// Onay talebi durumlarının adları ve rozet tonları (ADR-0027 Dilim 4). Durum değerleri sunucunun <c>ApprovalRequestStatus</c> adlarıdır.
/// Türkçe ad kaynak metindir (ADR-0034, dilim 16b); her okunuşta <c>admin.approval-status.{anahtar küçük harf}</c> ile çevrilir.
/// </summary>
public static class ApprovalLabels
{
    public const string StatusGroup = "admin.approval-status";

    internal static IReadOnlyList<(string Key, string Label)> StatusSource =>
    [
        ("Pending", "Bekliyor"),
        ("Approved", "Onaylandı"),
        ("Rejected", "Reddedildi"),
        ("Withdrawn", "Geri çekildi"),
        ("Invalidated", "Geçersizleşti"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Statuses => AdminLabels.Translate(StatusGroup, StatusSource);

    public static string StatusText(string status) => AdminLabels.Lookup(StatusGroup, StatusSource, status);

    public static string StatusClass(string status) => status switch
    {
        "Pending" => "stage-open",
        "Approved" => "stage-won",
        "Rejected" => "stage-lost",
        "Invalidated" => "stage-onhold",
        _ => "stage-cancelled",
    };
}
