using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
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

    /// <summary>Anahtar sözlükte yoksa (ya da sözlük hiç yüklenmediyse) verilen metni döner; anahtar adını göstermez.</summary>
    public static string GetOr(string key, string fallback) => current.TryGetValue(key, out var found) ? found : fallback;

    /// <summary>
    /// Yer tutucuların kalın gösterildiği metin (ör. "{0} sizi davet etti"). Değerler HTML'e kodlanır; sözlük metni ürüne aittir,
    /// kullanıcı girdisi yalnızca değer olarak girer.
    /// </summary>
    public static MarkupString Strong(string key, params string?[] values) =>
        new(Get(key, [.. values.Select(v => (object?)$"<strong>{WebUtility.HtmlEncode(v ?? string.Empty)}</strong>")]));
}
