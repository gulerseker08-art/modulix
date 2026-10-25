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

public sealed record ChoiceCategoryDto(string Key, string Name);

/// <param name="BuiltIn">Hazır değer: anahtarı ve kategorisi değişmez.</param>
public sealed record ChoiceValueDto(string Key, string Label, string? Category, bool Active, bool BuiltIn);

/// <summary>
/// Firmanın seçim listesi (Y9b), sırasıyla. Kayıtta <see cref="Field"/> alanının değeri bir <see cref="ChoiceValueDto.Key"/>'dir.
/// Pasif değer yeni kayıtta seçilemez; onu taşıyan kayıtta kalır. <see cref="Default"/> yeni kayıtta önerilen değerdir.
/// </summary>
public sealed record ChoiceListDto(
    string Key,
    string Name,
    string EntityKey,
    string Field,
    bool Required,
    IReadOnlyList<ChoiceCategoryDto> Categories,
    IReadOnlyList<ChoiceValueDto> Values,
    string? Default,
    bool Customized);

/// <summary>Listedeki değer; <see cref="Key"/> boşsa yeni değerdir ve <see cref="Category"/> seçilir.</summary>
public sealed record SaveChoiceValue(string? Key, string Label, string? Category = null, bool Active = true);

/// <summary>Listenin tamamı, istenen sırayla. Verilmeyen eski değer silinmez, pasif kalır.</summary>
public sealed record SaveChoiceListRequest(IReadOnlyList<SaveChoiceValue> Values);

/// <summary>Sektör profili (Y9c); <see cref="Modules"/> profilin önerdiği modüllerin anahtarları.</summary>
public sealed record SectorProfileDto(string Key, string Name, string Description, IReadOnlyList<string> Modules);

/// <summary>Firmanın profili (yoksa null) ve seçilebilecek profiller.</summary>
public sealed record SectorProfilesResponse(string? Current, DateTimeOffset? AppliedAt, IReadOnlyList<SectorProfileDto> Profiles);

/// <summary>Eklenecek (veya eklenen) alan: kayıt türünün adı, alanın adı, tipi ve seçenekleri.</summary>
public sealed record ProfileFieldDto(string EntityName, string Label, CustomFieldType Type, IReadOnlyList<string> Options);

public sealed record ProfileListValueDto(string ListName, string Label);

/// <summary>
/// Profilin firmaya ekleyeceği şeyler ("Şunlar eklenecek"). <see cref="ModulesToEnable"/> ve <see cref="UnavailableModules"/>
/// modül adlarıdır; kapalı kalan modülün alanları ve değerleri eklenmez. Mevcut alan ve değer değişmez.
/// </summary>
public sealed record ProfilePlanDto(
    string Profile,
    string ProfileName,
    IReadOnlyList<string> ModulesToEnable,
    IReadOnlyList<string> UnavailableModules,
    IReadOnlyList<ProfileFieldDto> Fields,
    IReadOnlyList<ProfileListValueDto> ListValues,
    bool IsEmpty);
