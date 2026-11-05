using System.Globalization;
using Modulix.Shared.Localization;

namespace Modulix.Web.Services;

/// <summary>
/// Arayüz metinleri (ADR-0034): etkin dilin sözlüğü. <see cref="SessionState"/> dil değişince sunucudan yükler
/// (<c>/api/i18n/{dil}</c>); eksik anahtarlar sunucuda Türkçeden tamamlanmıştır. Yüklenemeyen ya da tanımsız anahtar için
/// anahtarın kendisi gösterilir. Tarayıcıda tek kullanıcı çalıştığı için durum statiktir (bkz. <see cref="Regional"/>).
/// </summary>
public static class Texts
{
    private static IReadOnlyDictionary<string, string> current = new Dictionary<string, string>();

    /// <summary>Yüklü sözlüğün dili; hiç yüklenmediyse boş.</summary>
    public static string Language { get; private set; } = string.Empty;

    public static void Apply(TextBundleResponse bundle)
    {
        current = bundle.Texts;
        Language = bundle.Language;
    }

    public static string Get(string key, params object?[] args)
    {
        var text = current.TryGetValue(key, out var found) ? found : key;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
}
