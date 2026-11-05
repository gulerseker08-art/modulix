using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

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

/// <summary>Alan türleri. Biçimler: metin, mantıksal true/false.</summary>
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
