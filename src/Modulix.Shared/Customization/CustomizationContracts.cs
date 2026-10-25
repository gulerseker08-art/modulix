using System.Text.Json.Serialization;

namespace Modulix.Shared.Customization;

[JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))]
public enum CustomFieldType
{
    Text = 1,
    MultilineText = 2,
    Number = 3,
    Date = 4,
    YesNo = 5,
    Choice = 6,
    MultiChoice = 7,
}

public static class CustomFieldTypeLabels
{
    public static string Of(CustomFieldType type) => type switch
    {
        CustomFieldType.Text => "Metin",
        CustomFieldType.MultilineText => "Çok satırlı metin",
        CustomFieldType.Number => "Sayı",
        CustomFieldType.Date => "Tarih",
        CustomFieldType.YesNo => "Evet/hayır",
        CustomFieldType.Choice => "Seçim listesi",
        CustomFieldType.MultiChoice => "Çoklu seçim",
        _ => type.ToString(),
    };

    public static bool HasOptions(CustomFieldType type) => type is CustomFieldType.Choice or CustomFieldType.MultiChoice;
}

/// <summary>Özel alan eklenebilen kayıt türü (ör. "crm.opportunity", "Fırsat").</summary>
public sealed record CustomizableEntityDto(string Key, string Name);

public sealed record CustomFieldOptionDto(string Key, string Label, bool Active);

/// <summary>
/// Özel alan tanımı (Y9a). Kayıtlarda değer <c>custom.{Key}</c> altında durur: metin, sayı, "yyyy-MM-dd" tarih,
/// true/false, seçenek anahtarı ya da seçenek anahtarları dizisi.
/// </summary>
public sealed record CustomFieldDto(
    Guid Id,
    string EntityKey,
    string Key,
    string Label,
    CustomFieldType Type,
    bool Required,
    bool ShowInList,
    bool Archived,
    IReadOnlyList<CustomFieldOptionDto> Options);

/// <summary>Seçenek; <see cref="Key"/> boşsa yeni seçenektir. Listede verilmeyen eski seçenek pasif kalır.</summary>
public sealed record SaveCustomFieldOption(string? Key, string Label, bool Active = true);

/// <summary>Alan ekleme/düzenleme. <see cref="EntityKey"/> ve <see cref="Type"/> yalnızca eklerken kullanılır.</summary>
public sealed record SaveCustomFieldRequest(
    string EntityKey,
    string Label,
    CustomFieldType Type,
    bool Required = false,
    bool ShowInList = false,
    IReadOnlyList<SaveCustomFieldOption>? Options = null);

public sealed record ReorderCustomFieldsRequest(string EntityKey, IReadOnlyList<Guid> Ids);
