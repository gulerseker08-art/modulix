using System.Text;
using System.Text.RegularExpressions;

namespace Modulix.Modules.Marketing.Runs;

/// <summary>
/// İçerikteki kişiye özel değişkenler (ADR-0025, dilim 11), saf kurallar. Söz dizimi <c>{{ad|yedek metin}}</c>: değişken adı küçük harf, yedek metin <b>zorunludur</b>
/// (değer yoksa "Merhaba ," gibi boşluklar kalmasın). Desteklenen değişkenler: <c>ad</c>, <c>soyad</c>, <c>ad_soyad</c>, <c>firma</c>. Değerler düz metne konur;
/// satır sonu ve denetim karakterleri boşluğa çevrilir (konu satırında başlık enjeksiyonu olmaz), uzun değer kesilir. İçerik sürümü şablonu saklar; doldurma
/// yalnızca gönderim anında yapılır, bu yüzden onaylanan metin değişmez.
/// </summary>
public static partial class ContentTemplate
{
    public const string Name = "ad";
    public const string Surname = "soyad";
    public const string FullName = "ad_soyad";
    public const string Company = "firma";

    public const int FallbackMaxLength = 60;
    public const int ValueMaxLength = 100;

    public static IReadOnlyList<string> Variables { get; } = [Name, Surname, FullName, Company];

    /// <summary>Şablon hatalarını döndürür (boşsa geçerli). Söz diziminin dışında kalan "{{" ya da "}}" da hatadır.</summary>
    public static IReadOnlyList<string> Validate(string? text)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return errors;
        }

        foreach (Match match in Placeholder().Matches(text))
        {
            var name = match.Groups[1].Value;
            if (!Variables.Contains(name))
            {
                errors.Add($"Bilinmeyen değişken '{{{{{name}}}}}'. Kullanılabilir: {string.Join(", ", Variables)}.");
            }
            else if (!match.Groups[2].Success || string.IsNullOrWhiteSpace(match.Groups[2].Value))
            {
                errors.Add($"'{{{{{name}}}}}' için yedek metin gerekir (ör. {{{{{name}|Değerli müşterimiz}}}}).");
            }
            else if (match.Groups[2].Value.Any(char.IsControl))
            {
                errors.Add($"'{{{{{name}}}}}' yedek metni tek satır olmalıdır.");
            }
            else if (match.Groups[2].Value.Trim().Length > FallbackMaxLength)
            {
                errors.Add($"'{{{{{name}}}}}' yedek metni en fazla {FallbackMaxLength} karakter olabilir.");
            }
        }

        var rest = Placeholder().Replace(text, string.Empty);
        if (rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal))
        {
            errors.Add("Değişken söz dizimi geçersiz; biçim {{ad|yedek metin}} olmalıdır.");
        }

        return [.. errors.Distinct()];
    }

    /// <summary>Şablondaki değişkenleri doldurur; değer yoksa yedek metin kullanılır. Geçersiz şablon doldurulmaz (olduğu gibi döner).</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string>? values)
    {
        if (Validate(template).Count > 0)
        {
            return template;
        }

        return Placeholder().Replace(template, match =>
        {
            var fallback = Sanitize(match.Groups[2].Value) ?? string.Empty;
            return ValueOf(match.Groups[1].Value, values) ?? fallback;
        });
    }

    /// <summary>Önizleme/test için örnek değerler (gerçek kişi verisi değildir).</summary>
    public static IReadOnlyDictionary<string, string> Sample { get; } = new Dictionary<string, string>
    {
        [Name] = "Ayşe",
        [Surname] = "Yılmaz",
        [Company] = "Örnek A.Ş.",
    };

    private static string? ValueOf(string name, IReadOnlyDictionary<string, string>? values)
    {
        string? Get(string key) => values is not null && values.TryGetValue(key, out var raw) ? Sanitize(raw) : null;

        if (name != FullName)
        {
            return Get(name);
        }

        var parts = new[] { Get(Name), Get(Surname) }.Where(p => p is not null);
        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Satır sonu ve denetim karakterleri boşluğa, art arda boşluklar teke iner; boş kalırsa null; <see cref="ValueMaxLength"/> sonrası kesilir.</summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var lastSpace = false;
        foreach (var ch in value.Trim())
        {
            var space = char.IsControl(ch) || char.IsWhiteSpace(ch);
            if (space)
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(ch);
            }

            lastSpace = space;
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text.Length > ValueMaxLength ? text[..ValueMaxLength].TrimEnd() : text;
    }

    [GeneratedRegex(@"\{\{\s*([a-z_]+)\s*(?:\|([^{}]*?))?\s*\}\}")]
    private static partial Regex Placeholder();
}
