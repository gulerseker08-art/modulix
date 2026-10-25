using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Contracts.Customization;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Records;

/// <summary>
/// Bir kayıt alanı: JSON'daki teknik adı, doğrulaması ve entity'ye nasıl okunup yazıldığı.
/// Kayıt depoları alan listesinden doğrulama, okuma ve yazmayı türetir; yeni bir alan tek satırla eklenir.
/// </summary>
internal sealed class RecordField<TEntity>
{
    private readonly Func<JsonNode, ParsedValue> parse;
    private readonly Func<TEntity, JsonNode?> read;
    private readonly Action<TEntity, object?> write;

    public RecordField(
        string name,
        bool required,
        bool nullable,
        Func<JsonNode, ParsedValue> parse,
        Func<TEntity, JsonNode?> read,
        Action<TEntity, object?> write)
    {
        Name = name;
        Required = required;
        Nullable = nullable;
        this.parse = parse;
        this.read = read;
        this.write = write;
    }

    public string Name { get; }

    /// <summary>Oluştururken verilmesi zorunlu mu? Zorunlu olmayan alan verilmezse entity'nin varsayılanı kalır.</summary>
    public bool Required { get; }

    /// <summary>Alan boş (null) bırakılabilir mi?</summary>
    public bool Nullable { get; }

    /// <summary>Bağlı kayıt (veya kullanıcı) alanıysa, aktif tenant'ta var olup olmadığını denetler.</summary>
    public Func<CrmStoreContext, Guid, CancellationToken, Task<bool>>? Exists { get; init; }

    /// <summary>Bağlı kayıt bulunamazsa gösterilen mesaj.</summary>
    public string? NotFoundMessage { get; init; }

    /// <summary>Değeri firmanın seçim listesinden gelen alan (Y9b); depo değeri listeye göre denetler.</summary>
    public string? ChoiceListKey { get; init; }

    /// <summary>
    /// Boş bırakılamayan liste alanı (ör. aktivite tipi) boş gönderilirse verilmemiş sayılır: yeni kayıtta listenin
    /// varsayılanı atanır, güncellemede mevcut değer kalır. Böylece pasif bir hazır değer istemcinin varsayılanı olmaz.
    /// </summary>
    public bool IsOmitted(JsonNode? node) => ChoiceListKey is not null && !Nullable && IsEmpty(node);

    public ParsedValue Parse(JsonNode? node)
    {
        // Boş metin de boş değer sayılır; zorunlu alanlarda "zorunludur" hatası verir.
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

/// <summary>Alan türleri. Biçimler: metin, sayı, tarih "yyyy-MM-dd", zaman ISO 8601, liste değeri anahtarı ("Won"), kimlik GUID.</summary>
internal static class Field
{
    public static RecordField<T> Text<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set, bool required = false) =>
        new(name, required, nullable: !required, node => ParseText(node, maxLength), e => get(e), (e, v) => set(e, (string?)v));

    public static RecordField<T> Email<T>(string name, int maxLength, Func<T, string?> get, Action<T, string?> set) =>
        new(name, required: false, nullable: true, node =>
            {
                var text = ParseText(node, maxLength);
                return text.Error is null && text.Value is string value && !IsEmail(value)
                    ? ParsedValue.Fail("Geçerli bir e-posta adresi girin.")
                    : text;
            },
            e => get(e),
            (e, v) => set(e, (string?)v));

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

    public static RecordField<T> Currency<T>(string name, Func<T, string> get, Action<T, string> set) =>
        new(name, required: false, nullable: false, node =>
            {
                var text = ParseText(node, Opportunity.CurrencyLength);
                return text.Value is string code && code.Length == Opportunity.CurrencyLength && code.All(char.IsAsciiLetter)
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

    /// <summary>
    /// Firmanın seçim listesinden bir değer (Y9b, ör. fırsat aşaması). Burada yalnızca anahtarın biçimi denetlenir;
    /// listede olup olmadığını ve etkinliğini depo aktif tenant'ın listesine bakarak denetler.
    /// </summary>
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

    /// <summary>Yüzde: 0-100 arası tam sayı.</summary>
    public static RecordField<T> Percent<T>(string name, Func<T, int?> get, Action<T, int?> set) =>
        new(name, required: false, nullable: true, node =>
                node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue<int>(out var percent) && percent is >= 0 and <= 100
                    ? ParsedValue.Of(percent)
                    : ParsedValue.Fail("0 ile 100 arasında bir tam sayı girin."),
            e => get(e) is { } value ? JsonValue.Create(value) : null,
            (e, v) => set(e, (int?)v));

    /// <summary>Kaydın sorumlusu: aktif tenant'ın etkin üyesi olan bir kullanıcı.</summary>
    public static RecordField<T> Owner<T>(string name, Func<T, Guid?> get, Action<T, Guid?> set) =>
        new(name, required: false, nullable: true, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Members.IsActiveMemberAsync(id, ct),
            NotFoundMessage = "Kullanıcı bu kuruluşun üyesi değil.",
        };

    /// <summary>Aynı tenant'taki başka bir CRM kaydına bağ. Kayıt yoksa (veya başka tenant'ınsa) doğrulama hatası.</summary>
    public static RecordField<T> Reference<T, TTarget>(
        string name, string notFoundMessage, Func<T, Guid?> get, Action<T, Guid?> set, bool required = false)
        where TTarget : CrmEntity =>
        new(name, required, nullable: !required, ParseId, e => get(e) is { } value ? value.ToString() : null, (e, v) => set(e, (Guid?)v))
        {
            Exists = (context, id, ct) => context.Data.Set<TTarget>().AnyAsync(t => t.Id == id, ct),
            NotFoundMessage = notFoundMessage,
        };

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

    private static bool IsEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }
}
