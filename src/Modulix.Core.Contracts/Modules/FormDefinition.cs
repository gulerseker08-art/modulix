namespace Modulix.Core.Contracts.Modules;

/// <summary>
/// Bir kayıt türünün form düzeni tanımı (ADR-0021): sekmeler, bölümler ve alanların varsayılan yeri, genişliği ve
/// zorunluluğu. Tanım kodda sabittir (modül verir); firma yöneticisi bunun üzerine kendi düzenini kurar: alanları taşır,
/// yeni sekme ve bölüm açar, genişlik ve zorunluluk değiştirir, alan gizler. Alan anahtarları kayıt alanlarının teknik
/// adlarıdır (ör. "taxNumber"); özel alanlar <c>custom.{anahtar}</c> adıyla aynı düzende yer alır.
/// </summary>
public sealed record FormDefinition(
    string EntityKey,
    string DisplayName,
    IReadOnlyList<FormTabDefinition> Tabs,
    IReadOnlyList<FormSectionDefinition> Sections,
    IReadOnlyList<FormFieldDefinition> Fields)
{
    /// <summary>Her tanımın ilk sekmesi.</summary>
    public const string DefaultTab = "genel";

    /// <summary>Özel alanların varsayılan bölümü; her tanımda bulunmak zorundadır.</summary>
    public const string ExtrasSection = "ek";
}

public sealed record FormTabDefinition(string Key, string Label);

/// <param name="Icon">AppIcon adı (arayüz); boşsa ikon çizilmez.</param>
public sealed record FormSectionDefinition(string Key, string Label, string? Icon = null, string Tab = FormDefinition.DefaultTab);

/// <summary>
/// Formdaki bir alan. <see cref="Span"/> 12 kolonlu ızgarada genişliktir (3, 4, 6, 8, 12). <see cref="Required"/> kodun
/// zorunlu tuttuğu alandır; firma zorunluluğu artırabilir, azaltamaz. <see cref="Fixed"/> alan gizlenemez ve zorunluluğu
/// değiştirilemez (kaydın adı gibi kimlik alanları, yalnızca ekranda anlamı olan onay kutuları).
/// </summary>
public sealed record FormFieldDefinition(
    string Key,
    string Label,
    string Section,
    int Span = 6,
    bool Required = false,
    bool Fixed = false);

public static class FormSpans
{
    public static IReadOnlySet<int> Allowed { get; } = new HashSet<int> { 3, 4, 6, 8, 12 };
}
