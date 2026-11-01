using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Modules.Orders.Domain;

namespace Modulix.Modules.Orders.Records;

/// <summary>
/// Bir kayıt alanı: JSON'daki teknik adı, doğrulaması ve entity'ye nasıl okunup yazıldığı (CRM'deki kalıbın sadeleştirilmiş kopyası;
/// ortak araç takımına taşınması Ç0.2). Kayıt depoları alan listesinden doğrulama, okuma ve yazmayı türetir.
/// </summary>
internal sealed class RecordField<TEntity>(
    string name, bool required, bool nullable, Func<JsonNode, ParsedValue> parse, Func<TEntity, JsonNode?> read, Action<TEntity, object?> write)
{
    public string Name { get; } = name;

    /// <summary>Oluştururken verilmesi zorunlu mu?</summary>
    public bool Required { get; } = required;

    /// <summary>Alan boş (null) bırakılabilir mi?</summary>
    public bool Nullable { get; } = nullable;

    /// <summary>Bağlı kayıt (veya kullanıcı) alanıysa, aktif tenant'ta var olup olmadığını denetler.</summary>
    public Func<OrdersStoreContext, Guid, CancellationToken, Task<bool>>? Exists { get; init; }

    public string? NotFoundMessage { get; init; }

    /// <summary>Yalnızca oluştururken verilebilir; sonra değiştirilemez.</summary>
    public bool CreateOnly { get; init; }

    /// <summary>Yalnızca iş akışı eylemiyle yazılabilen alan (durum, toplam): genel uçlardan gelen değer sessizce yok sayılır.</summary>
    public bool SystemOnly { get; private set; }

    /// <summary>Değeri firmanın seçim listesinden gelen alan (Y9b).</summary>
    public string? ChoiceListKey { get; init; }

    public RecordField<TEntity> AsSystem()
    {
        SystemOnly = true;
        return this;
    }

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

/// <summary>Alan türleri. Biçimler: metin, sayı, tarih "yyyy-MM-dd", zaman ISO 8601, liste değeri anahtarı, kimlik GUID.</summary>
internal static class Field
{
    public static RecordField<T> Text<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set, bool required = false) =>
        new(name, required, nullable: !required, node => ParseText(node, maxLength), e => get(e), (e, v) => set(e, (string?)v));

    public static RecordField<T> Money<T>(string name, Func<T, decimal?> get, Action<T, decimal?> set) =>
        new(name, required: false, nullable: true, node =>
            {
                if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<decimal>(out var amount))
                {
                    return ParsedValue.Fail("Sayı olmalıdır.");
                }

                return amount is < 0 or > 9_999_999_999_999_999.99m || decimal.Round(amount, 2) != amount
                    ? ParsedValue.Fail("0 veya daha büyük, en fazla iki ondalıklı bir tutar girin.")
                    : ParsedValue.Of(amount);
            },
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (decimal?)v));

    /// <summary>Yüzde oranı: 0–100 arası, en çok 4 ondalık.</summary>
    public static RecordField<T> Rate<T>(string name, Func<T, decimal?> get, Action<T, decimal?> set) =>
        new(name, required: false, nullable: true, node =>
            {
                if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<decimal>(out var value))
                {
                    return ParsedValue.Fail("Sayı olmalıdır.");
                }

                return value is < 0 or > 100 || decimal.Round(value, 4) != value
                    ? ParsedValue.Fail("0 ile 100 arasında, en fazla dört ondalıklı bir oran girin.")
                    : ParsedValue.Of(value);
            },
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (decimal?)v));

    /// <summary>Negatif olmayan miktar ya da birim fiyat: 0 geçerli, en çok 6 ondalık.</summary>
    public static RecordField<T> Quantity<T>(string name, Func<T, decimal?> get, Action<T, decimal?> set) =>
        new(name, required: false, nullable: true, node =>
            {
                if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<decimal>(out var value))
                {
                    return ParsedValue.Fail("Sayı olmalıdır.");
                }

                return value is < 0 or > 999_999_999_999m || decimal.Round(value, 6) != value
                    ? ParsedValue.Fail("0 veya daha büyük, en fazla altı ondalıklı bir sayı girin.")
                    : ParsedValue.Of(value);
            },
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (decimal?)v));

    public static RecordField<T> Currency<T>(string name, Func<T, string> get, Action<T, string> set) =>
        new(name, required: false, nullable: false, node =>
            {
                var text = ParseText(node, SalesOrder.CurrencyLength);
                return text.Value is string code && code.Length == SalesOrder.CurrencyLength && code.All(char.IsAsciiLetter)
                    ? ParsedValue.Of(code.ToUpperInvariant())
                    : ParsedValue.Fail("Üç harfli para birimi kodu girin (ör. TRY).");
            },
            e => get(e),
            (e, v) => set(e, (string)v!));

    public static RecordField<T> Date<T>(string name, Func<T, DateOnly?> get, Action<T, DateOnly?> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String
                && DateOnly.TryParseExact(node.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? ParsedValue.Of(date)
                    : ParsedValue.Fail("Tarih yyyy-AA-gg biçiminde olmalıdır."),
            e => get(e) is { } value ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            (e, v) => set(e, (DateOnly?)v));

    public static RecordField<T> Time<T>(string name, Func<T, DateTimeOffset?> get, Action<T, DateTimeOffset?> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String
                && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                    ? ParsedValue.Of(time.ToUniversalTime())
                    : ParsedValue.Fail("Zaman ISO 8601 biçiminde olmalıdır."),
            e => get(e) is { } value ? value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null,
            (e, v) => set(e, (DateTimeOffset?)v));

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

    /// <summary>Negatif olmayan tam sayı (sıra, sürüm, gün, ay).</summary>
    public static RecordField<T> Integer<T>(string name, Func<T, int?> get, Action<T, int?> set, int max = int.MaxValue) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var value) && value >= 0 && value <= max
                    ? ParsedValue.Of(value)
                    : ParsedValue.Fail(max == int.MaxValue ? "0 veya daha büyük bir tam sayı girin." : $"0 ile {max} arasında bir tam sayı girin."),
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (int?)v));

    /// <summary>Kaydın sorumlusu: aktif tenant'ın etkin üyesi olan bir kullanıcı.</summary>
    public static RecordField<T> Owner<T>(string name, Func<T, Guid?> get, Action<T, Guid?> set) =>
        new(name, required: false, nullable: true, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Members.IsActiveMemberAsync(id, ct),
            NotFoundMessage = "Kullanıcı bu kuruluşun üyesi değil.",
        };

    /// <summary>Aynı tenant'taki başka bir sipariş kaydına bağ. Kayıt yoksa (veya başka tenant'ınsa) doğrulama hatası.</summary>
    public static RecordField<T> Reference<T, TTarget>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false, bool createOnly = false)
        where TTarget : OrdersEntity =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<TTarget>().AnyAsync(t => t.Id == id, ct),
            NotFoundMessage = notFoundMessage,
            CreateOnly = createOnly,
        };

    /// <summary>
    /// Ortak kayda bağ (ADR-0006: core.party / core.person). Tenant filtresi ve RLS sorguya uygulanır; başka tenant'ın ortak kaydı
    /// "bulunamadı" sayılır. Ortak kaydın okunabilirliği modülün bildirdiği okuma izniyle (sipariş okuma) sağlanır.
    /// </summary>
    public static RecordField<T> Shared<T, TShared>(string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false)
        where TShared : SharedEntity =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<TShared>().AnyAsync(s => s.Id == id, ct),
            NotFoundMessage = notFoundMessage,
        };

    /// <summary>Evet/hayır alanı; boş gönderilirse hayır.</summary>
    public static RecordField<T> Flag<T>(string name, Func<T, bool> get, Action<T, bool> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? ParsedValue.Of(node.GetValue<bool>())
                    : ParsedValue.Fail("true veya false olmalıdır."),
            e => JsonValue.Create(get(e)),
            (e, v) => set(e, v is true));

    /// <summary>Sabit anahtar listesinden tek değer. Boş gönderilirse verilmemiş sayılır: varsayılan/mevcut değer kalır.</summary>
    public static RecordField<T> Choice<T>(string name, IReadOnlyCollection<string> allowed, Func<T, string> get, Action<T, string> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Trim() is var key && allowed.Contains(key)
                    ? ParsedValue.Of(key)
                    : ParsedValue.Fail($"Şunlardan biri olmalıdır: {string.Join(", ", allowed)}."),
            e => get(e),
            (e, v) => { if (v is string key) { set(e, key); } });

    /// <summary>Sabit anahtar listesinden isteğe bağlı değer (boş bırakılabilir; boş gönderilirse temizlenir).</summary>
    public static RecordField<T> OptionalChoice<T>(string name, IReadOnlyCollection<string> allowed, Func<T, string?> get, Action<T, string?> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Trim() is var key && allowed.Contains(key)
                    ? ParsedValue.Of(key)
                    : ParsedValue.Fail($"Şunlardan biri olmalıdır: {string.Join(", ", allowed)}."),
            e => get(e),
            (e, v) => set(e, (string?)v));

    /// <summary>Boş olabilen kayıt kimliği; varlık denetimi yapmaz (bağı iş akışı servisi kendi doğrulamasıyla kurar).</summary>
    public static RecordField<T> Id<T>(string name, Func<T, Guid?> get, Action<T, Guid?> set) =>
        new(name, required: false, nullable: true, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v));

    private static ParsedValue ParseId(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String && FilterValue.Id(node.GetValue<string>()) is { } id
            ? ParsedValue.Of(id)
            : ParsedValue.Fail("Geçerli bir kayıt kimliği olmalıdır.");

    private static ParsedValue ParseText(JsonNode node, int maxLength)
    {
        if (node.GetValueKind() != JsonValueKind.String)
        {
            return ParsedValue.Fail("Metin olmalıdır.");
        }

        var text = node.GetValue<string>().Trim();
        return text.Length > maxLength
            ? ParsedValue.Fail($"En fazla {maxLength} karakter olabilir.")
            : ParsedValue.Of(text);
    }
}
