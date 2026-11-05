namespace Modulix.Core.Contracts.Sectors;

/// <summary>Faaliyet kataloğu düğüm türleri (ADR-0029): sektör, grup ve seçilebilir faaliyet.</summary>
public static class ActivityCodeKinds
{
    public const string Sector = "Sector";
    public const string Group = "Group";
    public const string Activity = "Activity";
}

/// <summary>Faaliyet kataloğundaki bir düğümün modüllere açılan özeti (ADR-0029); modüller yalnızca bu özeti görür.</summary>
public sealed record ActivityCodeInfo(string Code, string Name, string Kind, bool IsActive);

/// <summary>
/// Modüllerin faaliyet kataloğunu okuma sınırı: kayıtlarda saklanan faaliyet kodlarının katalogda var olup olmadığını denetlemek
/// için. Katalog platform geneli üründür; sonuçta kod verilenlerden yalnızca katalogda bulunanlar döner.
/// </summary>
public interface IActivityCodeCatalog
{
    Task<IReadOnlyList<ActivityCodeInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
}

/// <summary>
/// Kayıtta tek sütunda virgülle saklanan kod listesinin kuralları: küçük harf, rakam ve tire; tekrarsız ve sıralı; en çok
/// <see cref="MaxItems"/> kod. Firma ilişki alanları ve fırsat hizmet kapsamı aynı biçimi kullanır.
/// </summary>
public static class ActivityCodeList
{
    public const int MaxItems = 20;

    public const int CodeMaxLength = 50;

    /// <summary>Sütun uzunluğu: en çok <see cref="MaxItems"/> kod, kod başına en çok <see cref="CodeMaxLength"/> karakter ve ayraç.</summary>
    public const int StoredMaxLength = MaxItems * (CodeMaxLength + 1);

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length <= CodeMaxLength && code.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public static IReadOnlyList<string> Split(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : [.. stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Tekrarları atar, sıralar; geçersiz kod ya da fazla öğe varsa hata iletisi döner.</summary>
    public static (IReadOnlyList<string> Codes, string? Error) Normalize(IEnumerable<string?> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in codes)
        {
            var code = raw?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (!IsValidCode(code))
            {
                return ([], $"Geçersiz kod: '{code}'.");
            }

            result.Add(code);
        }

        return result.Count > MaxItems ? ([], $"En çok {MaxItems} seçim yapılabilir.") : ([.. result], null);
    }

    public static string? Join(IReadOnlyCollection<string> codes) => codes.Count == 0 ? null : string.Join(',', codes);
}
