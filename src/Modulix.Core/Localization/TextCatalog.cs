using System.Globalization;
using System.Text.Json;
using Modulix.Core.Contracts.Localization;
using Modulix.Core.Settings;

namespace Modulix.Core.Localization;

/// <summary>
/// <see cref="ITextCatalog"/>: sözlükler derlemeye gömülü düz JSON dosyalarıdır (<c>Localization/Texts/{dil}.json</c>,
/// anahtar → metin). Dosyalar açılışta bir kez okunur; sunucu kodu da arayüz de aynı kaynağı kullanır (ADR-0034).
/// </summary>
public sealed class TextCatalog : ITextCatalog
{
    private const string ResourcePrefix = "Modulix.Core.Localization.Texts.";

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> bundles = [];

    public TextCatalog()
    {
        var source = Load(LanguageOptions.Default);
        foreach (var language in LanguageOptions.Languages.Select(l => l.Value))
        {
            var merged = new Dictionary<string, string>(source, StringComparer.Ordinal);
            if (language != LanguageOptions.Default)
            {
                foreach (var (key, text) in Load(language))
                {
                    merged[key] = text;
                }
            }

            bundles[language] = merged;
        }
    }

    /// <summary>Bir dilin dosyasındaki ham metinler (Türkçeden tamamlanmamış); testler ve yükleyici içindir.</summary>
    public static IReadOnlyDictionary<string, string> Load(string language)
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + language + ".json")
            ?? throw new InvalidOperationException($"'{language}' dil sözlüğü derlemede bulunamadı.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"'{language}' dil sözlüğü okunamadı.");
    }

    public IReadOnlyDictionary<string, string> Bundle(string language) =>
        bundles[LanguageOptions.Normalize(language) ?? LanguageOptions.Default];

    public string Get(string language, string key, params object?[] args)
    {
        var text = Bundle(language).TryGetValue(key, out var found) ? found : key;
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
}
