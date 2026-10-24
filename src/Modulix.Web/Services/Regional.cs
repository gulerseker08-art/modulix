using System.Globalization;
using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Aktif firmanın bölgesel ayarları (Y6b): saatler firmanın saat diliminde, tarih ve sayılar firmanın biçiminde
/// gösterilir; yeni kayıtların para birimi buradan gelir. <see cref="SessionState"/> oturum ve firma değişince yükler.
/// Uygulama tek kullanıcılı tarayıcıda çalıştığı için durum statiktir; biçimlendiriciler (<see cref="CrmLabels"/>) okur.
/// </summary>
public static class Regional
{
    // Tarayıcıda tüm kültür verisi yüklenmez (indirme boyutu); biçimler burada sabitlenir.
    private static readonly Dictionary<string, (string Date, string Time, string Decimal, string Group)> Formats = new()
    {
        ["tr-TR"] = ("dd.MM.yyyy", "HH:mm", ",", "."),
        ["en-GB"] = ("dd/MM/yyyy", "HH:mm", ".", ","),
        ["en-US"] = ("MM/dd/yyyy", "h:mm tt", ".", ","),
    };

    private const string DefaultTimeZone = "Europe/Istanbul";

    public static TimeZoneInfo Zone { get; private set; } = FindZone(DefaultTimeZone);

    public static string DatePattern { get; private set; } = Formats["tr-TR"].Date;

    public static string TimePattern { get; private set; } = Formats["tr-TR"].Time;

    public static NumberFormatInfo Numbers { get; private set; } = NumberFormat("tr-TR");

    public static string Currency { get; private set; } = "TRY";

    public static void Apply(IReadOnlyList<SettingDto>? settings)
    {
        string Value(string key, string fallback) =>
            settings?.FirstOrDefault(s => s.Key == key)?.Value is { Length: > 0 } value ? value : fallback;

        Zone = FindZone(Value(SettingKeys.TimeZone, DefaultTimeZone));
        var format = Formats.TryGetValue(Value(SettingKeys.Format, "tr-TR"), out var found) ? found : Formats["tr-TR"];
        DatePattern = format.Date;
        TimePattern = format.Time;
        Numbers = NumberFormat(Value(SettingKeys.Format, "tr-TR"));
        Currency = Value(SettingKeys.Currency, "TRY");
    }

    /// <summary>Anın firmanın saat dilimindeki karşılığı.</summary>
    public static DateTimeOffset ToTenant(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone);

    /// <summary>Firmanın saat dilimindeki duvar saati (ör. formdaki tarih ve saat) → an.</summary>
    public static DateTimeOffset FromTenant(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified));
    }

    public static DateTimeOffset Now => ToTenant(DateTimeOffset.UtcNow);

    private static NumberFormatInfo NumberFormat(string key)
    {
        var format = Formats.TryGetValue(key, out var found) ? found : Formats["tr-TR"];
        return new NumberFormatInfo { NumberDecimalSeparator = format.Decimal, NumberGroupSeparator = format.Group };
    }

    private static TimeZoneInfo FindZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;
}
