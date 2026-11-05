namespace Modulix.Shared.Settings;

/// <summary>
/// Arayüz dili kodları (ADR-0034). Sunucudaki <c>LanguageOptions</c> ile aynı kümeyi taşır (Shared, Core'a başvuramaz;
/// ikisinin eşitliğini birim test denetler). Çözümleme sırası: kullanıcı tercihi → firma varsayılanı → <see cref="Default"/>.
/// </summary>
public static class LanguageCodes
{
    public const string Default = "tr";

    public static IReadOnlyList<(string Code, string Name)> All { get; } =
    [
        ("tr", "Türkçe"),
        ("en", "English"),
        ("az", "Azərbaycanca"),
    ];

    public static string? Normalize(string? code)
    {
        var trimmed = code?.Trim().ToLowerInvariant();
        return trimmed is not null && All.Any(l => l.Code == trimmed) ? trimmed : null;
    }

    /// <summary>Kültür adından ("az-Latn-AZ", "en_US", "tr") desteklenen dil kodu; desteklenmiyorsa <c>null</c>.</summary>
    public static string? FromCulture(string? culture) =>
        Normalize(culture?.Split('-', '_')[0]);

    public static string Resolve(string? userPreference, string? tenantDefault) =>
        Normalize(userPreference) ?? Normalize(tenantDefault) ?? Default;
}
