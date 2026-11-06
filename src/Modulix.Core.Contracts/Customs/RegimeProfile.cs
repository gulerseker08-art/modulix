using System.Text.RegularExpressions;

namespace Modulix.Core.Contracts.Customs;

/// <summary>Kural ihlalinin sonucu: uyarı kaydı engellemez, engel kaydı reddeder (ADR-0032 K7).</summary>
public enum RuleEnforcement
{
    Warn = 0,
    Block = 1,
}

/// <summary>
/// Tescil numarası kuralı (K7): uzunluk aralığı ve (isteğe bağlı) desen. Desen numaranın <b>tamamıyla</b> eşleşmelidir (başa ve sona
/// otomatik bağlanır). Kural koda yazılmaz; rejim profilinde tanımlanır ve profil sürümüyle değişir. Başlangıçta ihlal uyarıdır
/// (<see cref="RuleEnforcement.Warn"/>); uzman doğrulayınca profil sürümünde engele çevrilir. Tenant yalnızca sıkılaştırabilir (uyarıyı engele çevirir).
/// </summary>
/// <param name="Example">Geçerli örnek numara; kural kaydedilirken kurala uyduğu denetlenir.</param>
public sealed record RegistrationNoRule(string? Pattern, int? MinLength, int? MaxLength, string? Example, RuleEnforcement Enforcement);

/// <summary>
/// Rejim profili içeriği (K16), sürümlü ve JSON olarak saklanır. <see cref="SchemaVersion"/> içeriğin biçimidir; yeni bölümler (statü makinesi,
/// belge türleri, süre kuralı...) eklenince geriye uyumlu gelir ve ilgili dilimde yorumlanır. Bölümü olmayan kural uygulanmaz.
/// </summary>
/// <param name="RegimeCode">Profilin ait olduğu rejim kodu (örn. "7100"); bilgi amaçlıdır.</param>
public sealed record RegimeProfileContent(int SchemaVersion, string? RegimeCode, RegistrationNoRule? RegistrationNo)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Profil içeriği için saf kurallar (veritabanı gerekmez).</summary>
public static class RegimeProfileRules
{
    public const int PatternMaxLength = 200;
    public const int ExampleMaxLength = 30;
    public const int RegimeCodeMaxLength = 20;
    public const int RegistrationNoMaxLength = 30;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>İçeriğin hatalarını döner (boşsa geçerli). Desen derlenir ve örnek numarayla sınanır.</summary>
    public static IReadOnlyList<string> Validate(RegimeProfileContent? content)
    {
        if (content is null)
        {
            return ["Profil içeriği boş olamaz."];
        }

        var errors = new List<string>();
        if (content.SchemaVersion != RegimeProfileContent.CurrentSchemaVersion)
        {
            errors.Add($"Desteklenmeyen içerik sürümü: {content.SchemaVersion} (geçerli: {RegimeProfileContent.CurrentSchemaVersion}).");
        }

        if (content.RegimeCode is { } code && (code.Length is 0 or > RegimeCodeMaxLength || !code.All(char.IsAsciiLetterOrDigit)))
        {
            errors.Add($"Rejim kodu harf ve rakamdan oluşmalı ve en çok {RegimeCodeMaxLength} karakter olmalıdır.");
        }

        if (content.RegistrationNo is { } rule)
        {
            ValidateRegistrationNo(rule, errors);
        }

        return errors;
    }

    private static void ValidateRegistrationNo(RegistrationNoRule rule, List<string> errors)
    {
        if (!Enum.IsDefined(rule.Enforcement))
        {
            errors.Add("Tescil no kuralı: geçersiz uygulama düzeyi (Warn, Block).");
        }

        if (rule.MinLength is { } min && min is < 1 or > RegistrationNoMaxLength)
        {
            errors.Add($"Tescil no kuralı: en az uzunluk 1 ile {RegistrationNoMaxLength} arasında olmalıdır.");
        }

        if (rule.MaxLength is { } max && max is < 1 or > RegistrationNoMaxLength)
        {
            errors.Add($"Tescil no kuralı: en çok uzunluk 1 ile {RegistrationNoMaxLength} arasında olmalıdır (beyanname alanı {RegistrationNoMaxLength} karakterdir).");
        }

        if (rule.MinLength is { } lo && rule.MaxLength is { } hi && lo > hi)
        {
            errors.Add("Tescil no kuralı: en az uzunluk en çok uzunluktan büyük olamaz.");
        }

        if (rule.Example is { } example && (example.Length is 0 || example.Length > ExampleMaxLength))
        {
            errors.Add($"Tescil no kuralı: örnek en çok {ExampleMaxLength} karakter olabilir.");
        }

        if (rule.Pattern is { } pattern)
        {
            if (pattern.Length is 0 || pattern.Length > PatternMaxLength)
            {
                errors.Add($"Tescil no kuralı: desen boş olamaz ve en çok {PatternMaxLength} karakter olabilir.");
            }
            else if (!TryCompile(pattern, out _))
            {
                errors.Add("Tescil no kuralı: desen geçerli bir düzenli ifade değil.");
            }
        }

        // Örnek kuralın kendisine uymalıdır: yanlış bir desen yayınlanmadan yakalanır.
        if (errors.Count == 0 && rule.Example is { } sample && CheckRegistrationNo(rule, sample).Count > 0)
        {
            errors.Add("Tescil no kuralı: örnek numara kuralın kendisine uymuyor.");
        }
    }

    /// <summary>
    /// Tescil numarasını kurala göre denetler; ihlal iletilerini döner (boşsa uygun). Kural yoksa ya da desen zaman aşımına uğrarsa
    /// (yargılanamaz) ihlal sayılmaz.
    /// </summary>
    public static IReadOnlyList<string> CheckRegistrationNo(RegistrationNoRule? rule, string? registrationNo)
    {
        if (rule is null)
        {
            return [];
        }

        var value = registrationNo?.Trim() ?? string.Empty;
        var violations = new List<string>();
        if (rule.MinLength is { } min && value.Length < min)
        {
            violations.Add($"Tescil numarası en az {min} karakter olmalıdır.");
        }

        if (rule.MaxLength is { } max && value.Length > max)
        {
            violations.Add($"Tescil numarası en çok {max} karakter olmalıdır.");
        }

        if (rule.Pattern is { } pattern && TryCompile(pattern, out var regex))
        {
            try
            {
                if (!regex!.IsMatch(value))
                {
                    violations.Add(rule.Example is { } example
                        ? $"Tescil numarası beklenen biçime uymuyor (örnek: {example})."
                        : "Tescil numarası beklenen biçime uymuyor.");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Yargılanamayan desen engel olmasın.
            }
        }

        return violations;
    }

    /// <summary>Tenant yalnızca sıkılaştırabilir: sonuç profilin düzeyinden daha gevşek olamaz (Block, Warn'ı geçer).</summary>
    public static RuleEnforcement Tighten(RuleEnforcement profile, RuleEnforcement? tenant) =>
        profile == RuleEnforcement.Block || tenant == RuleEnforcement.Block ? RuleEnforcement.Block : RuleEnforcement.Warn;

    private static bool TryCompile(string pattern, out Regex? regex)
    {
        try
        {
            regex = new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }
}
