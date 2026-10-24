using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Settings;

/// <summary>Bölgesel ayarların seçenekleri (Y6). Saat dilimleri IANA adlarıdır; liste Türkiye'den çalışan firmalara göre seçilmiştir.</summary>
public static class RegionalOptions
{
    public const string DefaultTimeZone = "Europe/Istanbul";
    public const string DefaultCurrency = "TRY";
    public const string DefaultFormat = "tr-TR";

    public static IReadOnlyList<SettingOption> TimeZones { get; } =
    [
        new("Europe/Istanbul", "İstanbul (GMT+3)"),
        new("Europe/London", "Londra"),
        new("Europe/Berlin", "Berlin"),
        new("Europe/Amsterdam", "Amsterdam"),
        new("Europe/Paris", "Paris"),
        new("Europe/Athens", "Atina"),
        new("Europe/Moscow", "Moskova"),
        new("Asia/Baku", "Bakü"),
        new("Asia/Tbilisi", "Tiflis"),
        new("Asia/Dubai", "Dubai"),
        new("Asia/Riyadh", "Riyad"),
        new("America/New_York", "New York"),
        new("UTC", "UTC"),
    ];

    public static IReadOnlyList<SettingOption> Currencies { get; } =
    [
        new("TRY", "Türk lirası (₺)"),
        new("USD", "ABD doları ($)"),
        new("EUR", "Euro (€)"),
        new("GBP", "İngiliz sterlini (£)"),
    ];

    /// <summary>Kültür adları; tarih ve sayı biçimi bunlardan gelir.</summary>
    public static IReadOnlyList<SettingOption> Formats { get; } =
    [
        new("tr-TR", "Türkçe (31.12.2026, 1.234,56)"),
        new("en-GB", "İngilizce, Birleşik Krallık (31/12/2026, 1,234.56)"),
        new("en-US", "İngilizce, ABD (12/31/2026, 1,234.56)"),
    ];
}
