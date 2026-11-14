using Modulix.Shared.Lookups;
using Modulix.Shared.Records;
using Modulix.Shared.Warehouse;

namespace Modulix.Web.Services;

// Bu dosyadaki tablolarda Türkçe ad kaynak metindir (ADR-0034, dilim 14a); etkin dilin sözlüğünde `{grup}.{anahtar küçük harf}` yoksa o gösterilir.
// Tablolar özellik olarak tanımlıdır: metinler sınıf yüklenirken değil, her okunuşta çözülür (sözlük sonradan yüklenir). Yeni değer eklenince
// üç sözlüğe de anahtar eklenir (`WarehouseLabelsTests` kapsamı denetler).

/// <summary>Depo ekranlarının ortak etiket ve rozet kuralları. Rozet tonları yalnızca mevcut tasarım sınıflarından seçilir.</summary>
public static class WarehouseLabels
{
    public const string CharacterSetGroup = "warehouse.character-set";

    public static string KeyOf(string group, string key) => $"{group}.{key.Replace(':', '-').ToLowerInvariant()}";

    public static string Of(string group, string key, string fallback) => Texts.GetOr(KeyOf(group, key), fallback);

    /// <summary>Tabloyu her okunuşta çevirir.</summary>
    public static IReadOnlyList<(string Key, string Label)> Translate(string group, IReadOnlyList<(string Key, string Label)> source) =>
        [.. source.Select(x => (x.Key, Of(group, x.Key, x.Label)))];

    /// <summary>Etkin/pasif durum metni (liste rozetleri ve form özetleri).</summary>
    public static string ActiveText(bool active) => active ? Texts.GetOr("warehouse.active", "Etkin") : Texts.GetOr("common.inactive", "Pasif");

    /// <summary>Açık/kapalı ayar metni (gümrük uzantısı gibi anahtarlar).</summary>
    public static string OnOffText(bool on) => on ? Texts.GetOr("warehouse.on", "Açık") : Texts.GetOr("warehouse.off", "Kapalı");

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

    /// <summary>Hareket türü kategorisinin tonu: giriş başarı, çıkış dikkat, düzeltme nötr, transfer marka (kod kategoriye bakar, türün adına değil).</summary>
    public static string MovementBadge(string? category) => category switch
    {
        WarehouseAccess.MovementInbound => "status-customer",
        WarehouseAccess.MovementOutbound => "stage-negotiation",
        WarehouseAccess.MovementAdjustment => "status-inactive",
        _ => "stage-qualified",
    };

    /// <summary>Ondalık sayıyı Türkçe gösterimle yazar (binlik ayraçlı, gereksiz sıfırsız; en çok 3 ondalık).</summary>
    public static string Number(decimal value) => value.ToString("#,##0.###", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

    public static decimal Decimal(RecordResponse record, string field) => record.Values[field]?.GetValue<decimal>() ?? 0m;

    /// <summary>Seviye değerinde izin verilen karakter kümeleri (sunucudaki <c>LocationLevel.CharacterSets</c>).</summary>
    internal static readonly (string Key, string Label)[] CharacterSetSource =
    [
        ("alphanumeric", "Harf ve rakam"),
        ("letters", "Yalnızca harf"),
        ("digits", "Yalnızca rakam"),
    ];

    public static IReadOnlyList<(string Key, string Label)> CharacterSets => Translate(CharacterSetGroup, CharacterSetSource);

    /// <summary>Karakter kümesinin adı; tabloda olmayan anahtar olduğu gibi döner.</summary>
    public static string CharacterSetLabel(string? key)
    {
        foreach (var (k, label) in CharacterSetSource)
        {
            if (k == key)
            {
                return Of(CharacterSetGroup, k, label);
            }
        }

        return key ?? string.Empty;
    }

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
