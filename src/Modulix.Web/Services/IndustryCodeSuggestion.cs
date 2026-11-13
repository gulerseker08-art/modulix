using System.Text;

namespace Modulix.Web.Services;

/// <summary>
/// Sektör kataloğuna eklerken addan kod önerir (ADR-0043): Türkçe harfler sadeleştirilir, küçük harf, rakam ve "-" kalır
/// (sunucudaki kod kuralı), en çok 50 karakter. Öneridir; kullanıcı kodu düzenleyebilir, doğrulama sunucudadır.
/// </summary>
public static class IndustryCodeSuggestion
{
    private const int MaxLength = 50;

    private static readonly Dictionary<char, char> Folded = new()
    {
        ['ı'] = 'i', ['İ'] = 'i', ['ğ'] = 'g', ['Ğ'] = 'g', ['ü'] = 'u', ['Ü'] = 'u', ['ş'] = 's', ['Ş'] = 's',
        ['ö'] = 'o', ['Ö'] = 'o', ['ç'] = 'c', ['Ç'] = 'c', ['â'] = 'a', ['Â'] = 'a', ['î'] = 'i', ['Î'] = 'i', ['û'] = 'u', ['Û'] = 'u',
    };

    public static string FromName(string? name)
    {
        var builder = new StringBuilder();
        foreach (char ch in (name ?? string.Empty).Trim())
        {
            char c = char.ToLowerInvariant(Folded.GetValueOrDefault(ch, ch));
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length <= MaxLength ? slug : slug[..MaxLength].Trim('-');
    }
}
