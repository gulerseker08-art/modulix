using Modulix.Shared.Credentials;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>Süreli belge (ADR-0038 K3) etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class CredentialLabels
{
    public static string Status(string? key) => CredentialAccess.Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Geçerlilik aralığı: "01.01.2026 – 31.12.2026", yalnızca bitiş "31.12.2026'ya kadar", tarihsizse "Süresiz".</summary>
    public static string Validity(RecordResponse credential)
    {
        var from = FleetLabels.Day(WarehouseLabels.Text(credential, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(credential, "validTo"));
        return (from, to) switch
        {
            ({ } f, { } t) => $"{FleetLabels.Date(f)} – {FleetLabels.Date(t)}",
            (null, { } t) => $"{FleetLabels.Date(t)} tarihine kadar",
            ({ } f, null) => $"{FleetLabels.Date(f)} tarihinden itibaren",
            _ => "Süresiz",
        };
    }

    /// <summary>
    /// Belgenin bugünkü durumu: iptal nötr, süresi dolmuş kayıp tonu, henüz başlamamış marka tonu, <see cref="CredentialAccess.ExpiringDays"/> gün
    /// içinde bitecek dikkat tonu (kalan gün yazılır), geçerli başarı tonu. Eşik yalnızca görseldir; uyarı kuralı firmanın otomasyonundadır.
    /// </summary>
    public static (string Badge, string Text) State(RecordResponse credential, DateOnly today)
    {
        if (WarehouseLabels.Text(credential, "status") == CredentialAccess.Revoked)
        {
            return ("status-inactive", "İptal edildi");
        }

        var from = FleetLabels.Day(WarehouseLabels.Text(credential, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(credential, "validTo"));
        return (from, to) switch
        {
            (_, { } t) when t < today => ("stage-lost", "Süresi doldu"),
            ({ } f, _) when f > today => ("stage-qualified", "Başlamadı"),
            (_, { } t) when t == today => ("stage-negotiation", "Bugün bitiyor"),
            (_, { } t) when t <= today.AddDays(CredentialAccess.ExpiringDays) => ("stage-negotiation", $"{t.DayNumber - today.DayNumber} gün kaldı"),
            _ => ("status-customer", "Geçerli"),
        };
    }

    /// <summary>Dosya boyutu: "820 B", "14 KB", "2,4 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{(bytes / (1024d * 1024)).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} MB",
    };
}
