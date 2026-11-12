using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Logistics.Domain;

namespace Modulix.Modules.Logistics.Records;

/// <summary>
/// Bir kayıt alanı: JSON'daki teknik adı, doğrulaması ve entity'ye nasıl okunup yazıldığı (diğer modüllerdeki kalıbın kopyası;
/// ortak araç takımına taşınması Ç0.2). Kayıt depoları alan listesinden doğrulama, okuma ve yazmayı türetir. Yeni alan türleri
/// ihtiyaç doğdukça eklenir.
/// </summary>
internal sealed class RecordField<TEntity>(
    string name, bool required, bool nullable, Func<JsonNode, ParsedValue> parse, Func<TEntity, JsonNode?> read, Action<TEntity, object?> write)
{
    public string Name { get; } = name;

    /// <summary>Oluştururken verilmesi zorunlu mu?</summary>
    public bool Required { get; } = required;

    /// <summary>Alan boş (null) bırakılabilir mi?</summary>
    public bool Nullable { get; } = nullable;

    /// <summary>Bağlı kayıt alanıysa, aktif tenant'ta var olup olmadığını denetler.</summary>
    public Func<LogisticsStoreContext, Guid, CancellationToken, Task<bool>>? Exists { get; init; }

    public string? NotFoundMessage { get; init; }

    /// <summary>Yalnızca oluştururken verilebilir; sonra değiştirilemez.</summary>
    public bool CreateOnly { get; init; }

    /// <summary>Değeri firmanın seçim listesinden gelen alan (Y9b).</summary>
    public string? ChoiceListKey { get; init; }

    /// <summary>Yalnızca sunucunun hesapladığı alan (kod, derinlik): genel uçlardan gelen değer sessizce yok sayılır.</summary>
    public bool SystemOnly { get; private set; }

    public RecordField<TEntity> AsSystem()
    {
        SystemOnly = true;
        return this;
    }

    /// <summary>Liste alanı verilmediyse (boş) yazılmaz: yeni kayıtta listenin varsayılanı, güncellemede mevcut değer kalır.</summary>
    public bool IsOmitted(JsonNode? node) => ChoiceListKey is not null && !Nullable && IsEmpty(node);

    public ParsedValue Parse(JsonNode? node)
    {
        if (IsEmpty(node))
        {
            return Nullable ? ParsedValue.Of(null) : ParsedValue.Fail(Required ? "Bu alan zorunludur." : "Bu alan boş bırakılamaz.");
        }

        return parse(node);
    }

    private static bool IsEmpty([NotNullWhen(false)] JsonNode? node) =>
        node is null || (node.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(node.GetValue<string>()));

    public JsonNode? Read(TEntity entity) => read(entity);

    public void Write(TEntity entity, object? value) => write(entity, value);
}

internal readonly record struct ParsedValue(object? Value, string? Error)
{
    public static ParsedValue Of(object? value) => new(value, null);

    public static ParsedValue Fail(string error) => new(null, error);
}

/// <summary>Alan türleri. Biçimler: metin, mantıksal true/false, liste değeri anahtarı, kimlik GUID.</summary>
internal static class Field
{
    /// <summary><paramref name="upperCase"/> açıksa değer kırpıldıktan sonra büyük harfe çevrilir (kayıtlı değerle karşılaştırma da büyük harfle yapılır).</summary>
    public static RecordField<T> Text<T>(
        string name, int maxLength, Func<T, string?> get, Action<T, string?> set, bool required = false, bool createOnly = false, bool upperCase = false) =>
        new(name, required, nullable: !required, node => ParseText(node, maxLength, upperCase), e => get(e), (e, v) => set(e, (string?)v))
        {
            CreateOnly = createOnly,
        };

    /// <summary>Evet/hayır alanı; boş gönderilirse hayır.</summary>
    public static RecordField<T> Flag<T>(string name, Func<T, bool> get, Action<T, bool> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? ParsedValue.Of(node.GetValue<bool>())
                    : ParsedValue.Fail("true veya false olmalıdır."),
            e => JsonValue.Create(get(e)),
            (e, v) => set(e, v is true));

    /// <summary>Sınırlı aralıkta tam sayı. Zorunlu değilse boş bırakılabilir (verilmezse mevcut/varsayılan değer kalır).</summary>
    public static RecordField<T> Integer<T>(string name, Func<T, int?> get, Action<T, int?> set, int min, int max, bool required = false) =>
        new(name, required, nullable: !required, node =>
                node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var value) && value >= min && value <= max
                    ? ParsedValue.Of(value)
                    : ParsedValue.Fail($"{min} ile {max} arasında bir tam sayı girin."),
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (int?)v));

    /// <summary>Sabit anahtar listesinden tek değer (teknik doğrulama seçimi; tenant listesi değildir). Boş gönderilirse verilmemiş sayılır.</summary>
    public static RecordField<T> Choice<T>(string name, IReadOnlyCollection<string> allowed, Func<T, string> get, Action<T, string> set, bool createOnly = false) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Trim() is var key && allowed.Contains(key)
                    ? ParsedValue.Of(key)
                    : ParsedValue.Fail($"Şunlardan biri olmalıdır: {string.Join(", ", allowed)}."),
            e => get(e),
            (e, v) => { if (v is string key) { set(e, key); } })
        {
            CreateOnly = createOnly,
        };

    /// <summary>Firmanın seçim listesinden bir değer (Y9b). Listede olup olmadığını depo aktif tenant'ın listesine bakarak denetler.</summary>
    public static RecordField<T> ListChoice<T>(string name, string listKey, Func<T, string?> get, Action<T, string?> set, bool required = true) =>
        new(name, required: false, nullable: !required, node =>
                node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Trim() is var key && ChoiceValueKeys.IsValid(key)
                    ? ParsedValue.Of(key)
                    : ParsedValue.Fail("Listede olmayan değer."),
            e => get(e) is { Length: > 0 } value ? value : null,
            (e, v) => set(e, (string?)v))
        {
            ChoiceListKey = listKey,
        };

    /// <summary>Aynı tenant'taki başka bir lojistik kaydına bağ. Kayıt yoksa (veya başka tenant'ınsa) doğrulama hatası; varlığı belli edilmez.</summary>
    public static RecordField<T> Reference<T, TTarget>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false, bool createOnly = false)
        where TTarget : LogisticsEntity =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<TTarget>().AnyAsync(t => t.Id == id, ct),
            NotFoundMessage = notFoundMessage,
            CreateOnly = createOnly,
        };

    /// <summary>Başka modülün kaydına kimlikle bağ (ör. Filo aracı): yalnızca biçim denetlenir; varlığı ve uygunluğu kayıt deposu modülün okuma sözleşmesiyle denetler.</summary>
    public static RecordField<T> Id<T>(string name, Func<T, Guid?> get, Action<T, Guid?> set) =>
        new(name, required: false, nullable: true, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v));

    /// <summary>Ortak kişi kaydına bağ (ADR-0006: <c>core.person</c>); tenant filtresi ve RLS uygulanır, başka tenant'ın kişisi "bulunamadı" sayılır.</summary>
    public static RecordField<T> PersonRef<T>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false, bool createOnly = false) =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<Person>().AnyAsync(p => p.Id == id, ct),
            NotFoundMessage = notFoundMessage,
            CreateOnly = createOnly,
        };

    /// <summary>
    /// Ortak kayda (firma) bağ (ADR-0006: <c>core.party</c>). Tenant filtresi ve RLS sorguya uygulanır; başka tenant'ın ortak kaydı
    /// "bulunamadı" sayılır. Ortak kaydın okunabilirliği modülün bildirdiği okuma izniyle sağlanır.
    /// </summary>
    public static RecordField<T> PartyRef<T>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false, bool createOnly = false) =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<Party>().AnyAsync(p => p.Id == id, ct),
            NotFoundMessage = notFoundMessage,
            CreateOnly = createOnly,
        };

    /// <summary>
    /// Yalnızca sunucunun yazdığı (hesapladığı) alan: okunur, genel uçtan gelen değer sessizce yok sayılır. Hareketin zamanı, kullanıcısı ve bakiye miktarı gibi
    /// istemcinin belirleyemeyeceği değerler içindir.
    /// </summary>
    public static RecordField<T> Computed<T>(string name, Func<T, JsonNode?> read) =>
        new RecordField<T>(name, required: false, nullable: true, _ => ParsedValue.Of(null), read, (_, _) => { }).AsSystem();

    /// <summary>
    /// Ondalık sayı (JSON sayısı): <paramref name="min"/>-<paramref name="max"/> aralığında ve en çok <paramref name="scale"/> ondalık
    /// basamaklı (fazlası sessizce yuvarlanmaz, hata verir). Zorunlu değilse boş bırakılabilir.
    /// </summary>
    public static RecordField<T> Decimal<T>(
        string name, Func<T, decimal?> get, Action<T, decimal?> set, int scale, decimal min, decimal max, bool required = false) =>
        new(name, required, nullable: !required, node =>
            {
                if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<decimal>(out var value))
                {
                    return ParsedValue.Fail("Sayı olmalıdır.");
                }

                if (value < min || value > max)
                {
                    return ParsedValue.Fail($"{min.ToString(CultureInfo.InvariantCulture)} ile {max.ToString(CultureInfo.InvariantCulture)} arasında olmalıdır.");
                }

                return decimal.Round(value, scale) == value
                    ? ParsedValue.Of(value)
                    : ParsedValue.Fail($"En fazla {scale} ondalık basamak girilebilir.");
            },
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (decimal?)v));

    /// <summary>Tarih (yyyy-AA-gg). Zorunlu değilse boş bırakılabilir (boş gönderilirse temizlenir).</summary>
    public static RecordField<T> Date<T>(string name, Func<T, DateOnly?> get, Action<T, DateOnly?> set, bool required = false) =>
        new(name, required, nullable: !required, node =>
                node.GetValueKind() == JsonValueKind.String
                && DateOnly.TryParseExact(node.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? ParsedValue.Of(date)
                    : ParsedValue.Fail("Tarih yyyy-AA-gg biçiminde olmalıdır."),
            e => get(e) is { } value ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            (e, v) => set(e, (DateOnly?)v));

    /// <summary>Tarih ve saat (ISO 8601, saat dilimiyle; ör. 2026-11-12T08:30:00+03:00). Boş gönderilirse temizlenir.</summary>
    public static RecordField<T> Moment<T>(string name, Func<T, DateTimeOffset?> get, Action<T, DateTimeOffset?> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String
                && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? ParsedValue.Of(at)
                    : ParsedValue.Fail("Tarih ve saat ISO 8601 biçiminde olmalıdır (ör. 2026-11-12T08:30:00+03:00)."),
            e => get(e) is { } value ? value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture) : null,
            (e, v) => set(e, (DateTimeOffset?)v));

    private static ParsedValue ParseId(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String && Guid.TryParse(node.GetValue<string>(), out var id) && id != Guid.Empty
            ? ParsedValue.Of(id)
            : ParsedValue.Fail("Geçerli bir kayıt kimliği olmalıdır.");

    private static ParsedValue ParseText(JsonNode node, int maxLength, bool upperCase)
    {
        if (node.GetValueKind() != JsonValueKind.String)
        {
            return ParsedValue.Fail("Metin olmalıdır.");
        }

        var text = node.GetValue<string>().Trim();
        if (upperCase)
        {
            text = text.ToUpperInvariant();
        }

        return text.Length > maxLength
            ? ParsedValue.Fail($"En fazla {maxLength} karakter olabilir.")
            : ParsedValue.Of(text);
    }
}
