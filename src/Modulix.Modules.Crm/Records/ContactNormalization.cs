using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Arama ve eşleştirme için normalizasyon (ADR-0012). Orijinal değer her zaman ayrıca saklanır; normalleştirilmiş değer
/// yalnızca sorgu ve mükerrer önerisi içindir ve hiçbir yerde tekil kimlik sayılmaz.
/// </summary>
internal static partial class ContactNormalization
{
    public const string Phone = "Phone";
    public const string Email = "Email";

    /// <summary>E-posta: kırp, küçük harf. Biçim geçersizse null.</summary>
    public static string? NormalizeEmail(string value)
    {
        var text = value.Trim();
        var at = text.IndexOf('@');
        return at > 0 && at == text.LastIndexOf('@') && at < text.Length - 1 && !text.Any(char.IsWhiteSpace)
            ? text.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Telefon: yalnızca rakam ve "+"; Türkiye numaraları (0532..., 532..., 0090..., 90532...) "+90" biçimine getirilir.
    /// Rakam sayısı 7-15 değilse veya harf varsa null. Dahili/uzantı kabul edilmez (nota yazılır).
    /// </summary>
    public static string? NormalizePhone(string value)
    {
        var text = value.Trim();
        if (text.Length == 0 || !PhoneChars().IsMatch(text))
        {
            return null;
        }

        var plus = text.StartsWith('+');
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (plus)
        {
            // Uluslararası biçim olduğu gibi kalır.
        }
        else if (digits.StartsWith("00"))
        {
            digits = digits[2..];
            plus = true;
        }
        else if (digits.Length == 11 && digits[0] == '0')
        {
            digits = "90" + digits[1..];
            plus = true;
        }
        else if (digits.Length == 10 && digits[0] != '0')
        {
            digits = "90" + digits;
            plus = true;
        }
        else if (digits.Length == 12 && digits.StartsWith("90"))
        {
            plus = true;
        }

        return digits.Length is >= 7 and <= 15 ? (plus ? "+" : string.Empty) + digits : null;
    }

    /// <summary>E-posta mı telefon mu olduğu değerden anlaşılır; geçersizse null. Mükerrer eşleşmesi için normalleştirilmiş değer.</summary>
    public static string? IdentityValue(string value) => value.Contains('@') ? NormalizeEmail(value) : NormalizePhone(value);

    /// <summary>
    /// Eski API'den gelen serbest metin için yedek: geçerli biçime çevrilemeyen değer eski istemcileri bozmasın diye reddedilmez,
    /// yalnızca rakamlar (telefon) ya da küçük harf (e-posta) olarak normalleştirilir.
    /// </summary>
    public static string Lenient(string channel, string value) =>
        (channel == Phone ? NormalizePhone(value) : NormalizeEmail(value))
        ?? (channel == Phone ? new string(value.Where(char.IsAsciiDigit).ToArray()) : value.Trim().ToLowerInvariant());

    /// <summary>Metin araması için: küçük harf, aksansız (ş→s, ı→i...), tek boşluk.</summary>
    public static string FoldText(params string?[] parts)
    {
        var text = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)))
            .Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i')
            .ToLower(CultureInfo.InvariantCulture)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(ch);
        }

        return WhiteSpace().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    [GeneratedRegex(@"^[0-9+()\-./ ]+$")]
    private static partial Regex PhoneChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
