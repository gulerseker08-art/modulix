using Modulix.Shared.Settings;

namespace Modulix.Web.Services;

/// <summary>
/// Arayüzün etkin dili (ADR-0033): kullanıcı tercihi, yoksa aktif firmanın varsayılanı, yoksa Türkçe.
/// <see cref="SessionState"/> oturum ve firma değişince yükler. Metin çevirisi sonraki aşamadadır; bugün yalnızca dil seçilir.
/// </summary>
public static class UiLanguage
{
    public static string Code { get; private set; } = LanguageCodes.Default;

    public static void Apply(string? userPreference, IReadOnlyList<SettingDto>? settings)
    {
        var tenantDefault = settings?.FirstOrDefault(s => s.Key == SettingKeys.Language)?.Value;
        Code = LanguageCodes.Resolve(userPreference, tenantDefault);
    }
}
