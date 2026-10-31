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
