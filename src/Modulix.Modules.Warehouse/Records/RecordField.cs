using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Modules.Warehouse.Domain;

namespace Modulix.Modules.Warehouse.Records;

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
    public Func<WarehouseStoreContext, Guid, CancellationToken, Task<bool>>? Exists { get; init; }

    public string? NotFoundMessage { get; init; }

    /// <summary>Yalnızca oluştururken verilebilir; sonra değiştirilemez.</summary>
    public bool CreateOnly { get; init; }

    /// <summary>Değeri firmanın seçim listesinden gelen alan (Y9b).</summary>
    public string? ChoiceListKey { get; init; }

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
    public static RecordField<T> Text<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set, bool required = false) =>
        new(name, required, nullable: !required, node => ParseText(node, maxLength), e => get(e), (e, v) => set(e, (string?)v));

    /// <summary>Evet/hayır alanı; boş gönderilirse hayır.</summary>
    public static RecordField<T> Flag<T>(string name, Func<T, bool> get, Action<T, bool> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? ParsedValue.Of(node.GetValue<bool>())
                    : ParsedValue.Fail("true veya false olmalıdır."),
            e => JsonValue.Create(get(e)),
            (e, v) => set(e, v is true));

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

    /// <summary>Aynı tenant'taki başka bir depo kaydına bağ. Kayıt yoksa (veya başka tenant'ınsa) doğrulama hatası; varlığı belli edilmez.</summary>
    public static RecordField<T> Reference<T, TTarget>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false, bool createOnly = false)
        where TTarget : WarehouseEntity =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<TTarget>().AnyAsync(t => t.Id == id, ct),
            NotFoundMessage = notFoundMessage,
            CreateOnly = createOnly,
        };

    private static ParsedValue ParseId(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String && Guid.TryParse(node.GetValue<string>(), out var id) && id != Guid.Empty
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
