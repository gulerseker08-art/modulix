namespace Modulix.Core.Contracts.Customs;

/// <summary>
/// GTİP biçim kuralları (ADR-0032 K9): kod <b>metin</b> saklanır, yalnızca rakamdır; ilk 6 hane HS kodudur, 12–14 hane desteklenir.
/// Ayraçlar (nokta, boşluk) girişte atılır. Uzunluk sınırı yalnızca yapısaldır (6–14); hangi uzunluğun hangi tarife için geçerli olduğu
/// ve kodun varlığı katalog işidir ve doğrulanmamıştır (ADR bölüm 9), bu yüzden burada sert bir mevzuat kuralı yoktur. Hem depo
/// modülü (stok kartı) hem de platform kataloğu (içe aktarma) aynı kuralı kullanır.
/// </summary>
public static class GtipRules
{
    public const int MinLength = 6;
    public const int MaxLength = 14;

    /// <summary>Girişi normalleştirir; boşsa <c>(null, null)</c>, geçersizse hata iletisi döner.</summary>
    public static (string? Value, string? Error) Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (null, null);
        }

        var digits = new string([.. input.Where(c => c is not ('.' or ' ' or ' '))]);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return (null, "GTİP yalnızca rakamlardan oluşmalıdır (nokta ve boşluk atılır).");
        }

        return digits.Length is < MinLength or > MaxLength
            ? (null, $"GTİP {MinLength} ile {MaxLength} hane arasında olmalıdır.")
            : (digits, null);
    }
}

/// <summary>Bir kodun katalogdaki durumu. Yalnızca bilgi içindir: hiçbir durum kaydı engellemez (K9).</summary>
public enum GtipCatalogStatus
{
    /// <summary>Verilen tarihte yürürlükte katalog sürümü yok; kod hakkında bir şey söylenemez (uyarı üretilmez).</summary>
    NoCatalog = 0,

    /// <summary>Kod katalogda aynen var.</summary>
    Found = 1,

    /// <summary>Kod tam eşleşmez ama katalogdaki daha uzun kodların başıdır (ör. 6 haneli alt pozisyon); tam kodun girilmesi önerilir.</summary>
    PrefixOnly = 2,

    /// <summary>Kod katalogda yok: uyarı.</summary>
    NotFound = 3,
}

/// <param name="Status">Kodun durumu.</param>
/// <param name="Description">Katalogdaki eşya tanımı (yalnızca <see cref="GtipCatalogStatus.Found"/>).</param>
/// <param name="VersionName">Denetimin dayandığı katalog sürümünün adı (sürüm yoksa null).</param>
public sealed record GtipCheck(GtipCatalogStatus Status, string? Description, string? VersionName);

/// <summary>Katalogda arama sonucu.</summary>
public sealed record GtipSuggestion(string Code, string Description, string? Unit);

/// <summary>
/// Modüllerin GTİP kataloğunu okuma sözleşmesi. Katalog global ve yürürlük tarihlidir (ADR-0032 K9): sorgu tarihinde yürürlükteki en son
/// sürüm kullanılır. Katalog tenant verisi değildir; yalnızca okuma açılır, yönetim platformdadır.
/// </summary>
public interface IGtipCatalog
{
    /// <summary>Kodu (ayraçlar atılmış rakamlar) katalogda arar; <paramref name="day"/> tarihinde yürürlükteki sürüme bakar.</summary>
    Task<GtipCheck> CheckAsync(string code, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Kod ön ekine ya da tanım metnine göre en çok <paramref name="limit"/> öneri döner (<paramref name="day"/> tarihli sürümden).</summary>
    Task<IReadOnlyList<GtipSuggestion>> SearchAsync(string text, DateOnly day, int limit, CancellationToken cancellationToken);
}
