using System.Text;

namespace Modulix.Web.Services;

/// <summary>Firma adından kısa ad önerisi (ör. "Çağrı Lojistik A.Ş." → "cagri-lojistik-a-s"). Kuralı sunucu denetler.</summary>
public static class Slug
{
    private const int MaxLength = 50;

    public static string Suggest(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim())
        {
            var mapped = c switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' or 'I' or 'İ' or 'i' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' => 'u',
                _ => char.ToLowerInvariant(c),
            };

            if (mapped is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(mapped);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString();
        slug = slug.Length > MaxLength ? slug[..MaxLength] : slug;
        return slug.Trim('-');
    }
}
