using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Settings;

/// <summary>
/// Arayüz dilinin seçenekleri (ADR-0033). Dil, tarih/sayı biçiminden (<see cref="RegionalOptions.Formats"/>) ayrı bir tercihtir.
/// Çözümleme sırası: kullanıcı tercihi → firma varsayılanı → <see cref="Default"/>.
/// </summary>
public static class LanguageOptions
{
    public const string Default = "tr";

    /// <summary>Kullanıcı tercihinin sütun uzunluğu.</summary>
    public const int MaxLength = 5;

    public static IReadOnlyList<SettingOption> Languages { get; } =
    [
        new("tr", "Türkçe"),
        new("en", "English"),
        new("az", "Azərbaycanca"),
    ];

    public static bool IsSupported(string? code) => code is not null && Languages.Any(l => l.Value == code);

    /// <summary>Desteklenen bir kod ise onu, değilse (boş, bilinmeyen) <c>null</c> döner.</summary>
    public static string? Normalize(string? code)
    {
        var trimmed = code?.Trim().ToLowerInvariant();
        return IsSupported(trimmed) ? trimmed : null;
    }

    /// <summary>Kullanıcı tercihi → firma varsayılanı → <see cref="Default"/>; geçersiz değerler atlanır.</summary>
    public static string Resolve(string? userPreference, string? tenantDefault) =>
        Normalize(userPreference) ?? Normalize(tenantDefault) ?? Default;
}
