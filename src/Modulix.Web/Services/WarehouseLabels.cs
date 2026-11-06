using Modulix.Shared.Lookups;
using Modulix.Shared.Records;
using Modulix.Shared.Warehouse;

namespace Modulix.Web.Services;

/// <summary>Depo ekranlarının ortak etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class WarehouseLabels
{
    /// <summary>Etkin/açık için başarı tonu, pasif/kapalı için nötr ton.</summary>
    public static string ActiveBadge(bool active) => active ? "status-customer" : "status-inactive";

    /// <summary>Alan türü kategorisinin tonu: gümrük denetimli dikkat tonu, serbest marka tonu.</summary>
    public static string KindBadge(string? category) =>
        category == WarehouseAccess.ZoneKindControlled ? "stage-negotiation" : "stage-qualified";

    /// <summary>Beyanname durumu kategorisinin tonu: tescilli başarı, taslak marka, iptal nötr (kod kategoriye bakar, durumun adına değil).</summary>
    public static string DeclarationBadge(string? category) => category switch
    {
        WarehouseAccess.DeclarationRegistered => "status-customer",
        WarehouseAccess.DeclarationCancelled => "status-inactive",
        _ => "stage-qualified",
    };

    /// <summary>Ondalık sayıyı Türkçe gösterimle yazar (binlik ayraçlı, gereksiz sıfırsız; en çok 3 ondalık).</summary>
    public static string Number(decimal value) => value.ToString("#,##0.###", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    public static decimal Decimal(RecordResponse record, string field) => record.Values[field]?.GetValue<decimal>() ?? 0m;

    /// <summary>Seviye değerinde izin verilen karakter kümeleri (sunucudaki <c>LocationLevel.CharacterSets</c>).</summary>
    public static IReadOnlyList<(string Key, string Label)> CharacterSets { get; } =
    [
        ("alphanumeric", "Harf ve rakam"),
        ("letters", "Yalnızca harf"),
        ("digits", "Yalnızca rakam"),
    ];

    public static string CharacterSetLabel(string? key) => CharacterSets.FirstOrDefault(c => c.Key == key).Label ?? key ?? string.Empty;

    public static string Text(RecordResponse record, string field) => record.Values[field]?.GetValue<string>() ?? string.Empty;

    public static bool Flag(RecordResponse record, string field, bool fallback = false) => record.Values[field]?.GetValue<bool>() ?? fallback;

    public static int Number(RecordResponse record, string field) => record.Values[field]?.GetValue<int>() ?? 0;

    public static Guid? Id(RecordResponse record, string field) =>
        Guid.TryParse(record.Values[field]?.GetValue<string>(), out var id) ? id : null;

    /// <summary>
    /// Bir kayıt türünün kimlik → ad sözlüğü (listelerde tesis ve alan adı göstermek için). Genel kayıt uçlarından sayfa sayfa okunur;
    /// okuma izni yoksa ya da hata olursa boş kalır (ad yerine "—" gösterilir).
    /// </summary>
    public static async Task<Dictionary<Guid, string>> NamesAsync(ApiClient api, string entityKey, Func<RecordResponse, string> label)
    {
        var names = new Dictionary<Guid, string>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var result = await api.GetAsync<RecordPageResponse>($"/api/records/{entityKey}?page={pageNumber}&pageSize=200");
            if (result.Value is not { } chunk)
            {
                break;
            }

            foreach (var item in chunk.Items)
            {
                names[item.Id] = label(item);
            }

            if (names.Count >= chunk.TotalCount || chunk.Items.Count == 0)
            {
                break;
            }
        }

        return names;
    }

    /// <summary>
    /// Firma kimliklerini seçim kutusunun çözümleme ucundan ada çevirir (en çok 50'şer kimlik). Firmayı okuma izni yoksa ad dönmez ("—").
    /// Zaten bilinen kimlikler tekrar sorulmaz.
    /// </summary>
    public static async Task ResolveNamesAsync(ApiClient api, string lookupKey, IEnumerable<Guid> ids, IDictionary<Guid, string> names)
    {
        var missing = ids.Distinct().Where(id => !names.ContainsKey(id)).ToList();
        foreach (var chunk in missing.Chunk(50))
        {
            var resolved = await api.PostAsync<LookupResolveDto>($"/api/lookups/{lookupKey}/resolve", new ResolveLookupRequest(chunk));
            foreach (var item in resolved.Value?.Items ?? [])
            {
                names[item.Id] = item.Label ?? "—";
            }
        }
    }

    public static string NameOf(IReadOnlyDictionary<Guid, string> names, Guid? id) =>
        id is { } key && names.TryGetValue(key, out var name) ? name : "—";
}
