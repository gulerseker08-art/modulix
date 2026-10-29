using System.Security.Cryptography;

namespace Modulix.Modules.Crm.Records;

/// <summary>Lead'e özgü normalizasyon yardımcıları (numara üretimi, alan adı çıkarma).</summary>
internal static class LeadNormalization
{
    // Okunabilirlik için 0/O ve 1/I/L karışmaz.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// "LD-20261028-7K2M4Q": tarih + 6 rastgele karakter. Sıralı sayaç yarış koşulu yaratacağı için rastgele sonek kullanılır;
    /// çakışma (neredeyse imkânsız) benzersiz indeksle 409 olur ve istemci yeniden dener.
    /// </summary>
    public static string NewNumber(DateTimeOffset createdAt)
    {
        Span<char> suffix = stackalloc char[6];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"LD-{createdAt:yyyyMMdd}-{new string(suffix)}";
    }

    /// <summary>"https://www.Acme.com/x" → "acme.com"; alan adı çıkarılamazsa null.</summary>
    public static string? Domain(string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            return null;
        }

        var text = website.Trim();
        var host = Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri)
            ? uri.Host
            : text;
        host = host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host.Contains('.') && !host.Any(char.IsWhiteSpace) ? host : null;
    }
}
