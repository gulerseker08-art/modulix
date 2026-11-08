using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Automation;

namespace Modulix.Core.Automation;

/// <summary>
/// Kural aksiyonundaki metin parametrelerinde kayıt değişkeni (ADR-0027, Dilim 4d): <c>{{alan.anahtarı}}</c>. Değişken beyaz listedir:
/// yalnızca kayıt türünün kuralda okunabilen, kimliğin görebildiği alanları metne girer; ifade, işlev ya da iç içe şablon yoktur.
/// Yayında bilinmeyen/yetkisiz değişken engellenir; çalışırken eksik değer boş metin olur ve not düşülür. Değer tek geçişte
/// yerleştirilir: değerin içindeki <c>{{…}}</c> tekrar yorumlanmaz (şablon enjeksiyonu yok). Önizleme ve çalışma aynı kodu kullanır.
/// </summary>
public static partial class AutomationTextTemplate
{
    private static readonly NumberFormatInfo Turkish = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    /// <summary>Geçerli değişken: çift süslü parantez, boşluk payı, alan anahtarı (harf, rakam, nokta, alt çizgi, tire).</summary>
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>Metinde değişken var mı (ucuz ön kontrol)?</summary>
    public static bool HasVariables(string? text) => text is not null && Placeholder().IsMatch(text);

    /// <summary>Metindeki değişkenlerin alan anahtarları (tekrarsız, görünüş sırasıyla).</summary>
    public static IReadOnlyList<string> Variables(string? text) =>
        text is null ? [] : [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>Biçim hataları: kapanmamış ya da geçersiz değişken (<c>{{</c> / <c>}}</c> geçerli bir değişkenin parçası değil).</summary>
    public static IReadOnlyList<string> Errors(string? text)
    {
        if (text is null)
        {
            return [];
        }

        var rest = Placeholder().Replace(text, string.Empty);
        return rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal)
            ? ["Kapanmamış ya da geçersiz değişken var; değişken {{alan}} biçiminde olmalı."]
            : [];
    }

    /// <summary>
    /// Alan metne girebilir mi? Bilinmeyen, görülemeyen ve metne anlamlı çevrilemeyen (kayıt seçimi, tarih-saat) alanlar yasaktır;
    /// ileti kullanıcıya gösterilir. Uygunsa <c>null</c>.
    /// </summary>
    public static string? CheckVariable(AutomationFieldSet fields, string key)
    {
        if (fields.Find(key) is not { } field)
        {
            return $"Bilinmeyen değişken: '{key}'.";
        }

        if (!fields.CanRead(field))
        {
            return $"'{field.Label}' alanını görme izniniz yok; metne eklenemez.";
        }

        return field.Type switch
        {
            AutomationFieldType.Lookup => $"'{field.Label}' alanı (kayıt seçimi) metne eklenemez.",
            AutomationFieldType.DateTime => $"'{field.Label}' alanı (tarih ve saat) metne eklenemez; tarih alanı kullanın.",
            _ => null,
        };
    }

    /// <summary>
    /// Değişkenleri kaydın değerleriyle doldurur. Eksik (boş) ya da görülemeyen değer boş metin olur ve <paramref name="notes"/>'a
    /// not düşer. <paramref name="encodeHtml"/>: sonuç HTML'e girecekse yerleştirilen değerler kodlanır (şablonun kendi metni değil).
    /// </summary>
    public static string Render(string text, AutomationFieldSet fields, IAutomationFacts facts, ICollection<string> notes, bool encodeHtml = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(notes);

        return Placeholder().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            var field = fields.Find(key);
            if (field is not null && !fields.CanRead(field))
            {
                // Yayından sonra kuralın kimliği alan iznini kaybetmiş olabilir; değer asla metne girmez ve notta görünmez.
                notes.Add($"'{field.Label}' bu kayıtta görülemiyor; metinde boş bırakıldı.");
                return string.Empty;
            }

            if (field is null || CheckVariable(fields, key) is not null)
            {
                notes.Add($"'{key}' değişkeni kullanılamıyor; boş bırakıldı.");
                return string.Empty;
            }

            var fact = facts.Get(key, AutomationValueSource.New);
            if (fact.State != AutomationFactState.Value || fact.Value is null)
            {
                notes.Add($"'{field.Label}' bu kayıtta {(fact.State == AutomationFactState.Hidden ? "görülemiyor" : "boş")}; metinde boş bırakıldı.");
                return string.Empty;
            }

            var value = Format(field, fact.Value);
            return encodeHtml ? WebUtility.HtmlEncode(value) : value;
        });
    }

    /// <summary>Değeri okunur metne çevirir: seçim etiketi, Türkçe sayı, "Evet/Hayır", gg.AA.yyyy tarih.</summary>
    public static string Format(AutomationFieldDefinition field, object value) => value switch
    {
        string text when field.Type == AutomationFieldType.Choice => field.Options.FirstOrDefault(o => o.Key == text)?.Label ?? text,
        string text => text,
        decimal number => number.ToString("#,##0.##", Turkish),
        AutomationMoney money => $"{money.Amount.ToString("#,##0.00", Turkish)} {money.Currency}",
        bool flag => flag ? "Evet" : "Hayır",
        DateOnly date => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
