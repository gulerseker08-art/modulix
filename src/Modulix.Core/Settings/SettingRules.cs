using System.Net.Mail;
using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Settings;

/// <summary>Ayar değerinin tanımına uygunluğu (Y6). Hem ayarlar uç noktası hem kayıt deposu bunu kullanır.</summary>
public static partial class SettingRules
{
    /// <summary>Değer geçerliyse <c>null</c>, değilse kullanıcıya gösterilecek hata.</summary>
    public static string? Validate(SettingDefinition definition, string? value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (value is null)
        {
            return "Değer metin olmalıdır.";
        }

        if (value.Length == 0)
        {
            return definition.Required ? $"{definition.DisplayName} zorunludur." : null;
        }

        if (value != value.Trim())
        {
            return "Değer boşlukla başlayamaz veya bitemez.";
        }

        if (value.Length > Math.Min(definition.MaxLength, TenantSetting.ValueMaxLength))
        {
            return $"En fazla {definition.MaxLength} karakter olabilir.";
        }

        if (definition.Type != SettingType.MultilineText && value.Contains('\n', StringComparison.Ordinal))
        {
            return "Tek satır olmalıdır.";
        }

        return definition.Type switch
        {
            SettingType.Choice when definition.Options.All(o => o.Value != value) => "Listedeki seçeneklerden biri olmalıdır.",
            SettingType.Email when !IsEmail(value) => "Geçerli bir e-posta adresi girin.",
            SettingType.Phone when !PhonePattern().IsMatch(value) => "Geçerli bir telefon numarası girin.",
            _ when definition.Pattern is { } pattern && !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) =>
                definition.PatternMessage ?? "Değer beklenen biçimde değil.",
            _ => null,
        };
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value, out var address) && address.Address == value && value.Contains('.', StringComparison.Ordinal);

    // Rakam, boşluk, artı, tire ve parantez; en az 7 rakam.
    [GeneratedRegex(@"^(?=(?:\D*\d){7})[0-9 +()\-]{7,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}
