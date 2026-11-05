using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Arayüzün etkin dili (ADR-0034): kullanıcı tercihi, yoksa aktif firmanın varsayılanı, yoksa Türkçe.
/// <see cref="SessionState"/> oturum ve firma değişince yükler. Metin çevirisi sonraki aşamadadır; bugün yalnızca dil seçilir.
/// </summary>
public static class UiLanguage
{
    public static string Code { get; private set; } = LanguageCodes.Default;

    /// <param name="fallback">
    /// Firma varsayılanı yoksa (oturum ya da firma seçilmemiş, giriş öncesi) kullanılacak dil: tarayıcıda hatırlanan ya da
    /// tarayıcının dili.
    /// </param>
    public static void Apply(string? userPreference, IReadOnlyList<SettingDto>? settings, string? fallback = null)
    {
        var tenantDefault = settings?.FirstOrDefault(s => s.Key == SettingKeys.Language)?.Value;
        Code = LanguageCodes.Resolve(userPreference, tenantDefault ?? fallback);
    }
}
