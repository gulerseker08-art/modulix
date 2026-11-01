using System.Globalization;

namespace Modulix.Web.Services;

/// <summary>Sipariş ekranlarının etiketleri; anahtarlar sunucudaki <c>OrderStatus</c>, <c>HandoverStatus</c> ve <c>FulfillmentStatus</c> ile aynıdır.</summary>
public static class OrderLabels
{
    public static IReadOnlyList<(string Key, string Label)> Statuses { get; } =
    [
        ("Draft", "Taslak"), ("PendingApproval", "Onay bekliyor"), ("Approved", "Onaylandı"), ("Cancelled", "İptal edildi"), ("Closed", "Kapatıldı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Handovers { get; } =
    [
        ("NotStarted", "Devir başlamadı"), ("Preparing", "Devir hazırlanıyor"), ("Ready", "Devre hazır"), ("Sent", "Operasyona gönderildi"),
        ("InfoRequested", "Bilgi bekleniyor"), ("Accepted", "Operasyon kabul etti"),
    ];

    public static IReadOnlyList<(string Key, string Label)> Fulfillments { get; } =
    [
        ("NotStarted", "Başlamadı"), ("Planned", "Planlandı"), ("Partial", "Kısmen gerçekleşti"), ("Completed", "Tamamlandı"),
    ];

    public static IReadOnlyList<(string Key, string Label)> LineKinds { get; } =
        [("Product", "Ürün"), ("Service", "Hizmet"), ("Package", "Paket"), ("Free", "Serbest kalem")];

    public static IReadOnlyList<(string Key, string Label)> AcceptanceMethods { get; } =
        [("Portal", "Müşteri sayfasından"), ("Manual", "Satış kullanıcısı kaydetti")];

    public static IReadOnlyList<(string Key, string Label)> Sorts { get; } =
    [
        ("-createdAt", "En yeni"), ("createdAt", "En eski"), ("number", "Numara (artan)"), ("-number", "Numara (azalan)"),
        ("deliveryDate", "Teslim tarihi (yakın)"), ("-deliveryDate", "Teslim tarihi (uzak)"), ("title", "Başlık (A-Z)"),
    ];

    public static string Label(IReadOnlyList<(string Key, string Label)> list, string? key) =>
        list.FirstOrDefault(x => x.Key == key).Label ?? key ?? "—";

    public static string StatusBadge(string status) => status switch
    {
        "Approved" => "won",
        "PendingApproval" => "onhold",
        "Cancelled" => "cancelled",
        "Closed" => "lost",
        _ => "open",
    };

    public static string HandoverBadge(string status) => status switch
    {
        "Accepted" => "won",
        "Sent" or "Ready" => "onhold",
        "InfoRequested" => "lost",
        _ => "open",
    };

    public static string FulfillmentBadge(string status) => status switch
    {
        "Completed" => "won",
        "Partial" or "Planned" => "onhold",
        _ => "open",
    };

    public static string History(string kind) => kind switch
    {
        "CreatedFromQuote" => "Kabul edilmiş tekliften oluşturuldu",
        _ => kind,
    };

    /// <summary>Tutar; izin yoksa (null) gizli olduğu söylenir. Para birimleri karıştırılmaz: her tutar kendi para biriminde gösterilir.</summary>
    public static string Money(decimal? amount, string currency) =>
        amount is { } value ? $"{value.ToString("N2", Regional.Numbers)} {currency}" : "Gizli";

    public static string Quantity(decimal value, string? unit) => $"{value.ToString("G29", Regional.Numbers)} {unit}".TrimEnd();

    public static string Date(DateOnly? date) => date is { } d ? d.ToString(Regional.DatePattern, CultureInfo.InvariantCulture) : "—";
}
