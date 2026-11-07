namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Sözleşme listesinin sıralama anahtarları (ADR-0022, dilim 9). Önüne "-" konursa azalan. Tutar sütunları sıralanamaz: hassas alanın sırası değerini sızdırır.</summary>
public static class ContractSort
{
    public const string Default = "-createdAt";

    public static IReadOnlyList<string> Fields { get; } = ["createdAt", "title", "number", "startDate", "endDate", "noticeDeadline"];

    public static IReadOnlyList<string> All { get; } = [.. Fields, .. Fields.Select(f => "-" + f)];

    public static bool IsValid(string? sort) => sort is not null && All.Contains(sort, StringComparer.Ordinal);
}

/// <summary>
/// Sözleşme listesinin süzgeç sözlüğü: sorgu dizgisi biçiminde (<c>effectiveStatus=Active&amp;sort=-endDate</c>) yalnızca bilinen anahtarlar ve geçerli
/// değerler kabul edilir; böylece saklanan bir süzgeç yetki ya da tenant sınırı taşıyamaz (liste uç noktası her açılışta yetkiyi yeniden uygular).
/// Göreli tarihli hızlı görünümler (bitişi yaklaşan vb.) tarih yerine <c>preset</c> anahtarıyla saklanır; bayatlamazlar. Kullanıcının kişisel görünümleri
/// ortak <c>core.GridViews</c> tablosundadır (ADR-0036); modüle özel görünüm tablosu kaldırıldı.
/// </summary>
public static class ContractViewRules
{
    public const int FiltersMaxLength = 500;

    /// <summary>Hızlı görünümler: sorumlu benim, imza bekleyen, bitişi 90 gün içinde, bildirim süresi 30 gün içinde, taahhüdü geciken, satış sözleşmeleri (satış kategorisindeki türler, <see cref="ContractScopes"/>).</summary>
    public static IReadOnlyList<string> Presets { get; } = ["mine", "signature", "ending", "notice", "overdue", "sales"];

    public static IReadOnlyList<string> Keys { get; } = ["preset", "effectiveStatus", "preparationStatus", "approvalStatus", "signatureStatus", "type", "currency", "renewalMode", "search", "sort"];

    /// <summary>Ayrıştırır; geçersiz anahtar/değer için hata metinleri döner (boş liste = geçerli). Aynı anahtar iki kez geçerse hata.</summary>
    public static (IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Errors) Parse(string? filters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(filters))
        {
            return (values, errors);
        }

        if (filters.Length > FiltersMaxLength)
        {
            errors.Add($"Süzgeç metni en fazla {FiltersMaxLength} karakter olabilir.");
            return (values, errors);
        }

        foreach (var part in filters.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            if (!Keys.Contains(key, StringComparer.Ordinal))
            {
                errors.Add($"Bilinmeyen süzgeç: {key}.");
            }
            else if (!values.TryAdd(key, value))
            {
                errors.Add($"Süzgeç tekrarlanamaz: {key}.");
            }
            else if (ValueError(key, value) is { } error)
            {
                errors.Add(error);
            }
        }

        return (values, errors);
    }

    private static string? ValueError(string key, string value) => key switch
    {
        "preset" => Presets.Contains(value, StringComparer.Ordinal) ? null : "Bilinmeyen hızlı görünüm.",
        "effectiveStatus" => EffectiveStatus.All.Contains(value) ? null : "Geçersiz yürürlük durumu.",
        "preparationStatus" => PreparationStatus.All.Contains(value) ? null : "Geçersiz hazırlık durumu.",
        "approvalStatus" => ApprovalStatus.All.Contains(value) ? null : "Geçersiz onay durumu.",
        "signatureStatus" => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts && parts.All(SignatureStatus.All.Contains) ? null : "Geçersiz imza durumu.",
        "renewalMode" => RenewalMode.All.Contains(value) ? null : "Geçersiz yenileme yöntemi.",
        "currency" => value.Length == Domain.Contract.CurrencyLength && value.All(char.IsAsciiLetterUpper) ? null : "Para birimi 3 harfli kod olmalıdır.",
        "type" => value.Length is > 0 and <= 50 ? null : "Geçersiz sözleşme türü.",
        "search" => value.Length is > 0 and <= 100 ? null : "Arama metni 1-100 karakter olmalıdır.",
        "sort" => ContractSort.IsValid(value) ? null : "Geçersiz sıralama.",
        _ => "Bilinmeyen süzgeç.",
    };
}
