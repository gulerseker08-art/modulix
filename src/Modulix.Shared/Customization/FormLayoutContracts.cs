namespace Modulix.Shared.Customization;

// Form düzeni (ADR-0021): formların sekme / bölüm / alan yerleşimi. GET /api/form-layouts her üyeye açıktır (formlar buradan
// çizilir); düzenleme /api/customization/form-layouts/{kayıtTürü} core.customization.manage ister.

public sealed record FormTabDto(string Key, string Label);

/// <param name="Icon">AppIcon adı; boşsa ikon çizilmez.</param>
public sealed record FormSectionDto(string Key, string Label, string? Icon, string Tab);

/// <summary>
/// Formdaki bir alan. <see cref="Span"/> 12 kolonlu ızgarada genişlik (3, 4, 6, 8, 12). <see cref="Fixed"/> alan gizlenemez
/// ve zorunluluğu değişmez; <see cref="DefaultRequired"/> kodun zorunlu tuttuğu alandır (azaltılamaz). <see cref="IsCustom"/>
/// özel alan (anahtar <c>custom.{anahtar}</c>).
/// </summary>
public sealed record FormFieldDto(
    string Key,
    string Label,
    string Section,
    int Span,
    bool Required,
    bool Visible,
    bool Fixed,
    bool IsCustom,
    bool DefaultRequired);

/// <summary>Bir formun etkin düzeni. <see cref="Customized"/> firma düzeni var; <see cref="Version"/> kaydederken geri verilir.</summary>
public sealed record FormLayoutDto(
    string EntityKey,
    string EntityName,
    IReadOnlyList<FormTabDto> Tabs,
    IReadOnlyList<FormSectionDto> Sections,
    IReadOnlyList<FormFieldDto> Fields,
    bool Customized,
    long Version);

public sealed record SaveFormFieldRequest(string Key, string Section, int Span, bool Required, bool Visible);

/// <summary>Düzenin tamamı gönderilir; bütün alanlar bir kez yer almalıdır. Eski <see cref="Version"/> 409 döner.</summary>
public sealed record SaveFormLayoutRequest(
    IReadOnlyList<FormTabDto> Tabs,
    IReadOnlyList<FormSectionDto> Sections,
    IReadOnlyList<SaveFormFieldRequest> Fields,
    long Version);

public static class FormSpanLabels
{
    public static IReadOnlyList<int> All { get; } = [3, 4, 6, 8, 12];

    public static string Of(int span) => span switch
    {
        3 => "Çeyrek",
        4 => "Üçte bir",
        6 => "Yarım",
        8 => "Üçte iki",
        12 => "Tam satır",
        _ => span.ToString(),
    };
}
