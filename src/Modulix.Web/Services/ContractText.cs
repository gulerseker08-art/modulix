namespace Modulix.Web.Services;

/// <summary>
/// Sözleşme modülü etiket tablolarının etkin dildeki karşılıkları (ADR-0034, dilim 10a). Tablodaki Türkçe ad kaynak metindir; sözlükte
/// <c>{grup}.{anahtar küçük harf}</c> yoksa o gösterilir (boş anahtar <c>onetime</c> olur). Yeni değer eklenince üç sözlüğe de anahtar eklenir
/// (<c>ContractLabelsTests</c> kapsamı denetler).
/// </summary>
public static class ContractText
{
    public static string KeyOf(string group, string key) => $"{group}.{(key.Length == 0 ? "onetime" : key.ToLowerInvariant())}";

    public static string Of(string group, string key, string fallback) => Texts.GetOr(KeyOf(group, key), fallback);

    /// <summary>Tabloyu her okunuşta çevirir: metinler sınıf yüklenirken değil, okunurken çözülür (sözlük sonradan yüklenir).</summary>
    public static IReadOnlyList<(string Key, string Label)> Translate(string group, IReadOnlyList<(string Key, string Label)> source) =>
        [.. source.Select(x => (x.Key, Of(group, x.Key, x.Label)))];

    /// <summary>Tablodaki karşılığı çevirir; tabloda olmayan anahtar olduğu gibi döner.</summary>
    public static string Lookup(string group, IReadOnlyList<(string Key, string Label)> source, string key) =>
        source.Any(x => x.Key == key) ? Of(group, key, source.First(x => x.Key == key).Label) : key;
}
