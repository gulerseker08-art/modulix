using System.Globalization;
using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Arayüzün etkin dili (ADR-0034): kullanıcı tercihi, yoksa aktif firmanın varsayılanı, yoksa Türkçe.
/// <see cref="SessionState"/> oturum ve firma değişince yükler. Metinler <see cref="Texts"/> sözlüğünden gelir; ay ve gün adları
/// (örn. "9 Kasım 2026 Pazartesi") <see cref="Culture"/> ile yazılır. Sayı ve saat/tarih düzeni dilden bağımsız, firmanın biçim
/// ayarındandır (<see cref="Regional"/>).
/// </summary>
public static class UiLanguage
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    public static string Code { get; private set; } = LanguageCodes.Default;

    /// <summary>Etkin dilin kültürü: ay ve gün adlarını yazmak için. Tarayıcıda kültür verisi yoksa Türkçeye düşer.</summary>
    public static CultureInfo Culture { get; private set; } = TurkishCulture;

    /// <param name="fallback">
    /// Firma varsayılanı yoksa (oturum ya da firma seçilmemiş, giriş öncesi) kullanılacak dil: tarayıcıda hatırlanan ya da
    /// tarayıcının dili.
    /// </param>
    public static void Apply(string? userPreference, IReadOnlyList<SettingDto>? settings, string? fallback = null)
    {
        var tenantDefault = settings?.FirstOrDefault(s => s.Key == SettingKeys.Language)?.Value;
        Code = LanguageCodes.Resolve(userPreference, tenantDefault ?? fallback);
        Culture = CultureOf(Code);
    }

    /// <summary>Dil kodunun kültürü: İngilizce için en-GB (gün-ay-yıl sırası, diğer dillerle uyumlu).</summary>
    public static CultureInfo CultureOf(string code)
    {
        var name = code switch
        {
            "en" => "en-GB",
            "az" => "az-Latn-AZ",
            _ => "tr-TR",
        };

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return TurkishCulture;
        }
    }
}
