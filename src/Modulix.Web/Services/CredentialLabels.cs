using Modulix.Shared.Credentials;
using Modulix.Shared.Records;

namespace Modulix.Web.Services;

/// <summary>Belgenin bugünkü durumu (metinden bağımsız; sayım ve rozet kararları metne bakmaz).</summary>
public enum CredentialState
{
    Revoked,
    Expired,
    NotStarted,
    DueToday,
    Expiring,
    Valid,
}

/// <summary>Süreli belge (ADR-0038 K3) etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class CredentialLabels
{
    /// <summary>Belge durumu seçenekleri her okunuşta etkin dile göre kurulur (Türkçe kaynak metin sözlükte yoksa yedek kalır).</summary>
    public static IReadOnlyList<(string Key, string Label)> Statuses => WarehouseLabels.Translate("credentials.status", CredentialAccess.Statuses);

    public static string Status(string? key) => Statuses.FirstOrDefault(s => s.Key == key).Label ?? key ?? string.Empty;

    /// <summary>Geçerlilik aralığı: "01.01.2026 – 31.12.2026", yalnızca bitiş "31.12.2026 tarihine kadar", tarihsizse "Süresiz".</summary>
    public static string Validity(RecordResponse credential)
    {
        var from = FleetLabels.Day(WarehouseLabels.Text(credential, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(credential, "validTo"));
        return (from, to) switch
        {
            ({ } f, { } t) => Texts.Get("credentials.validity.range", FleetLabels.Date(f), FleetLabels.Date(t)),
            (null, { } t) => Texts.Get("credentials.validity.until", FleetLabels.Date(t)),
            ({ } f, null) => Texts.Get("credentials.validity.since", FleetLabels.Date(f)),
            _ => Texts.Get("credentials.validity.open-ended"),
        };
    }

    /// <summary>
    /// Belgenin bugünkü durumu: iptal, süresi dolmuş, henüz başlamamış, bugün bitiyor, <see cref="CredentialAccess.ExpiringDays"/> gün içinde
    /// bitecek ya da geçerli. Eşik yalnızca görseldir; uyarı kuralı firmanın otomasyonundadır.
    /// </summary>
    public static CredentialState StateOf(RecordResponse credential, DateOnly today)
    {
        if (WarehouseLabels.Text(credential, "status") == CredentialAccess.Revoked)
        {
            return CredentialState.Revoked;
        }

        var from = FleetLabels.Day(WarehouseLabels.Text(credential, "validFrom"));
        var to = FleetLabels.Day(WarehouseLabels.Text(credential, "validTo"));
        return (from, to) switch
        {
            (_, { } t) when t < today => CredentialState.Expired,
            ({ } f, _) when f > today => CredentialState.NotStarted,
            (_, { } t) when t == today => CredentialState.DueToday,
            (_, { } t) when t <= today.AddDays(CredentialAccess.ExpiringDays) => CredentialState.Expiring,
            _ => CredentialState.Valid,
        };
    }

    /// <summary>
    /// Belgenin rozeti: iptal nötr, süresi dolmuş kayıp tonu, henüz başlamamış marka tonu, bitişi yaklaşan dikkat tonu (kalan gün yazılır),
    /// geçerli başarı tonu.
    /// </summary>
    public static (string Badge, string Text) State(RecordResponse credential, DateOnly today)
    {
        var state = StateOf(credential, today);
        return state switch
        {
            CredentialState.Revoked => ("status-inactive", Texts.Get("credentials.state.revoked")),
            CredentialState.Expired => ("stage-lost", Texts.Get("credentials.state.expired")),
            CredentialState.NotStarted => ("stage-qualified", Texts.Get("credentials.state.not-started")),
            CredentialState.DueToday => ("stage-negotiation", Texts.Get("credentials.state.due-today")),
            CredentialState.Expiring => ("stage-negotiation", Texts.Get("credentials.state.days-left", DaysLeft(credential, today))),
            _ => ("status-customer", Texts.Get("credentials.state.valid")),
        };
    }

    private static int DaysLeft(RecordResponse credential, DateOnly today) =>
        FleetLabels.Day(WarehouseLabels.Text(credential, "validTo")) is { } to ? to.DayNumber - today.DayNumber : 0;

    /// <summary>Dosya boyutu: "820 B", "14 KB", "2,4 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{(bytes / (1024d * 1024)).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} MB",
    };
}
