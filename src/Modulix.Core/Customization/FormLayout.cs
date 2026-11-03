using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;

namespace Modulix.Core.Customization;

/// <summary>
/// Firmanın bir formun düzeni için yaptığı özelleştirme (ADR-0021). Satır yoksa ya da <see cref="LayoutJson"/> boşsa
/// modülün tanımı (<see cref="FormDefinition"/>) geçerlidir. "Varsayılana dön" satırı silmez, değeri boşaltır: sürüm geri
/// sarmaz, eşzamanlı düzenleme sessizce ezilmez. Tenant verisidir; Row-Level Security ile korunur.
/// </summary>
public sealed class TenantFormLayout : ITenantEntity, ICompanyShared
{
    public const int EntityKeyMaxLength = 100;

    private TenantFormLayout()
    {
        // EF Core için
    }

    public TenantFormLayout(string entityKey)
    {
        Id = Guid.CreateVersion7();
        EntityKey = entityKey;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Kaydederken aktif tenant'tan atanır; uygulama kodu yazmaz.</summary>
    public Guid TenantId { get; private set; }

    public string EntityKey { get; private set; } = string.Empty;

    /// <summary>Firmanın düzeni (<see cref="FormLayoutInput"/> JSON'u); null ise tanım geçerlidir.</summary>
    public string? LayoutJson { get; private set; }

    /// <summary>Her değişiklikte artar; eşzamanlı düzenleme denetimi içindir.</summary>
    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public void Set(string? layoutJson, Guid? userId)
    {
        LayoutJson = layoutJson;
        UpdatedBy = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
        Version++;
    }
}

public sealed record FormTabLayout(string Key, string Label);

public sealed record FormSectionLayout(string Key, string Label, string? Icon, string Tab);

/// <summary>
/// Formdaki bir alanın etkin düzeni. <see cref="DefaultRequired"/> kodun (ya da özel alan tanımının) zorunluluğu;
/// <see cref="Required"/> firmanın düzeniyle birlikte etkin zorunluluk. <see cref="IsCustom"/> alan özel alandır
/// (anahtar <c>custom.{anahtar}</c>).
/// </summary>
public sealed record FormFieldLayout(
    string Key,
    string Label,
    string Section,
    int Span,
    bool Required,
    bool Visible,
    bool Fixed,
    bool IsCustom,
    bool DefaultRequired);

/// <summary>Bir formun etkin düzeni: tanım + firmanın özelleştirmesi (varsa) + özel alanlar.</summary>
public sealed record FormLayoutInfo(
    string EntityKey,
    string DisplayName,
    IReadOnlyList<FormTabLayout> Tabs,
    IReadOnlyList<FormSectionLayout> Sections,
    IReadOnlyList<FormFieldLayout> Fields,
    bool Customized,
    long Version);

/// <summary>Yöneticinin kaydettiği alan yerleşimi.</summary>
public sealed record FormFieldInput(string Key, string Section, int Span, bool Required, bool Visible);

/// <summary>Yöneticinin kaydettiği düzen; firmanın satırında JSON olarak saklanır.</summary>
public sealed record FormLayoutInput(
    IReadOnlyList<FormTabLayout> Tabs,
    IReadOnlyList<FormSectionLayout> Sections,
    IReadOnlyList<FormFieldInput> Fields);

/// <summary>
/// Form düzenleri (ADR-0021), aktif tenant için. Okuma her üyeye açıktır (formlar buradan çizilir); yazma
/// <c>core.customization.manage</c> ister (API denetler). Değişiklikler <c>core.form-layout</c> kaydı olarak denetim
/// kaydına yazılır.
/// </summary>
public interface IFormLayoutService
{
    /// <summary>Açık modüllerin formları, etkin düzenleriyle.</summary>
    Task<IReadOnlyList<FormLayoutInfo>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Tanımsız ya da modülü kapalı form için null.</summary>
    Task<FormLayoutInfo?> FindAsync(string entityKey, CancellationToken cancellationToken);

    /// <summary>Düzeni kaydeder; doğrulama hataları alan adlarıyla döner, eski sürüm çakışmadır.</summary>
    Task<RecordResult<FormLayoutInfo>> SaveAsync(string entityKey, FormLayoutInput input, long version, CancellationToken cancellationToken);

    /// <summary>Firmanın düzenini kaldırır, tanım geçerli olur; satır kalır ve sürüm ilerler.</summary>
    Task<RecordResult<FormLayoutInfo>> ResetAsync(string entityKey, long version, CancellationToken cancellationToken);
}

/// <summary>Düzenin çözümlenmesi ve doğrulanması. Veritabanı bilmez; birim testleriyle denetlenir.</summary>
public static partial class FormLayoutRules
{
    public const int LabelMaxLength = 60;
    public const int KeyMaxLength = 30;
    public const int MaxTabs = 8;
    public const int MaxSections = 24;

    /// <summary>Özel alanın düzen anahtarı.</summary>
    public static string CustomKey(string fieldKey) => RecordFields.CustomPrefix + fieldKey;

    /// <summary>
    /// Etkin düzen. Firmanın düzeni yoksa tanım sırası geçerlidir. Varsa: sekme ve bölümler firmanınki; alanlar firmanın
    /// yerleşimiyle, düzende olmayan (sonradan eklenen özel alan gibi) alanlar varsayılan bölümlerinde sona eklenir;
    /// bölümü artık yoksa ilk bölüme düşer. Sabit alan gizlenemez; zorunluluk azaltılamaz.
    /// </summary>
    public static FormLayoutInfo Resolve(FormDefinition definition, IReadOnlyList<CustomFieldInfo> customFields, FormLayoutInput? stored, long version)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(customFields);
        var defaults = DefaultFields(definition, customFields);

        if (stored is null || stored.Tabs.Count == 0 || stored.Sections.Count == 0)
        {
            return new FormLayoutInfo(
                definition.EntityKey,
                definition.DisplayName,
                [.. definition.Tabs.Select(t => new FormTabLayout(t.Key, t.Label))],
                [.. definition.Sections.Select(s => new FormSectionLayout(s.Key, s.Label, s.Icon, s.Tab))],
                defaults,
                Customized: false,
                version);
        }

        var tabs = stored.Tabs.Select(t => new FormTabLayout(t.Key, t.Label)).ToList();

        // Eski tek sekmeli düzen: firma sekme eklememiş, yalnızca alan/bölüm özelleştirmiş. Her kayıt formu sekmelidir (arayüz kuralı);
        // bu yüzden modülün varsayılan sekmelerine yükseltilir: tanımdaki bölümler kendi sekmesine geçer, firmanın kendi bölümleri
        // ilk sekmede kalır. Firma kendi sekmesini eklemişse (birden fazla sekme) düzenine dokunulmaz.
        var upgraded = tabs.Count == 1 && tabs[0].Key == FormDefinition.DefaultTab && definition.Tabs.Count > 1;
        if (upgraded)
        {
            tabs = [.. definition.Tabs.Select(t => new FormTabLayout(t.Key, t.Label))];
        }

        var definitionTabOf = definition.Sections.ToDictionary(s => s.Key, s => s.Tab, StringComparer.Ordinal);
        var firstTab = tabs[0].Key;
        var sections = stored.Sections
            .Select(s => new FormSectionLayout(
                s.Key,
                s.Label,
                s.Icon,
                upgraded && definitionTabOf.TryGetValue(s.Key, out var definitionTab) ? definitionTab : tabs.Any(t => t.Key == s.Tab) ? s.Tab : firstTab))
            .ToList();
        var firstSection = sections[0].Key;
        var byKey = defaults.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var fields = new List<FormFieldLayout>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in stored.Fields)
        {
            if (!byKey.TryGetValue(input.Key, out var field) || !placed.Add(input.Key))
            {
                continue;
            }

            fields.Add(field with
            {
                Section = sections.Any(s => s.Key == input.Section) ? input.Section : firstSection,
                Span = FormSpans.Allowed.Contains(input.Span) ? input.Span : field.Span,
                Required = field.DefaultRequired || (!field.Fixed && input.Required),
                Visible = field.Fixed || input.Visible,
            });
        }

        foreach (var field in defaults.Where(f => !placed.Contains(f.Key)))
        {
            fields.Add(field with { Section = sections.Any(s => s.Key == field.Section) ? field.Section : firstSection });
        }

        return new FormLayoutInfo(definition.EntityKey, definition.DisplayName, tabs, sections, fields, Customized: true, version);
    }

    /// <summary>Kaydedilecek düzenin hataları; boşsa geçerlidir. Alan adları hata iletisinin hedefidir.</summary>
    public static IReadOnlyList<RecordFieldError> Validate(FormDefinition definition, IReadOnlyList<CustomFieldInfo> customFields, FormLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(customFields);
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<RecordFieldError>();

        if (input.Tabs.Count is 0 or > MaxTabs)
        {
            errors.Add(new("tabs", $"1 ile {MaxTabs} arasında sekme olmalıdır."));
        }

        var tabKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Tabs.Count; i++)
        {
            var tab = input.Tabs[i];
            if (!IsValidKey(tab.Key) || !tabKeys.Add(tab.Key))
            {
                errors.Add(new($"tabs[{i}].key", "Sekme anahtarı küçük harf, rakam ve tire içermeli ve tekil olmalıdır."));
            }

            if (!IsValidLabel(tab.Label))
            {
                errors.Add(new($"tabs[{i}].label", $"Sekme adı 1-{LabelMaxLength} karakter olmalıdır."));
            }
        }

        if (input.Sections.Count is 0 or > MaxSections)
        {
            errors.Add(new("sections", $"1 ile {MaxSections} arasında bölüm olmalıdır."));
        }

        var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Sections.Count; i++)
        {
            var section = input.Sections[i];
            if (!IsValidKey(section.Key) || !sectionKeys.Add(section.Key))
            {
                errors.Add(new($"sections[{i}].key", "Bölüm anahtarı küçük harf, rakam ve tire içermeli ve tekil olmalıdır."));
            }

            if (!IsValidLabel(section.Label))
            {
                errors.Add(new($"sections[{i}].label", $"Bölüm adı 1-{LabelMaxLength} karakter olmalıdır."));
            }

            if (!tabKeys.Contains(section.Tab))
            {
                errors.Add(new($"sections[{i}].tab", "Bölüm var olan bir sekmeye bağlı olmalıdır."));
            }

            if (section.Icon is { Length: > KeyMaxLength })
            {
                errors.Add(new($"sections[{i}].icon", "İkon adı çok uzun."));
            }
        }

        var defaults = DefaultFields(definition, customFields).ToDictionary(f => f.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Fields.Count; i++)
        {
            var field = input.Fields[i];
            if (!defaults.TryGetValue(field.Key, out var definitionField))
            {
                errors.Add(new($"fields[{i}].key", $"Bilinmeyen alan: {field.Key}."));
                continue;
            }

            if (!seen.Add(field.Key))
            {
                errors.Add(new($"fields[{i}].key", $"Alan düzende iki kez yer alıyor: {field.Key}."));
            }

            if (!sectionKeys.Contains(field.Section))
            {
                errors.Add(new($"fields[{i}].section", "Alan var olan bir bölüme bağlı olmalıdır."));
            }

            if (!FormSpans.Allowed.Contains(field.Span))
            {
                errors.Add(new($"fields[{i}].span", "Genişlik 3, 4, 6, 8 ya da 12 olmalıdır."));
            }

            if (definitionField.Fixed && (!field.Visible || field.Required != definitionField.DefaultRequired))
            {
                errors.Add(new($"fields[{i}].key", $"Sabit alan gizlenemez ve zorunluluğu değiştirilemez: {definitionField.Label}."));
            }
            else if (definitionField.DefaultRequired && !field.Required)
            {
                errors.Add(new($"fields[{i}].required", $"Zorunlu alan isteğe bağlı yapılamaz: {definitionField.Label}."));
            }
        }

        foreach (var missing in defaults.Values.Where(f => !seen.Contains(f.Key)))
        {
            errors.Add(new("fields", $"Alan düzende eksik: {missing.Label} ({missing.Key})."));
        }

        return errors;
    }

    /// <summary>Firmanın düzeniyle zorunlu olmuş alanlar (tanımın zaten zorunlu tuttukları hariç); boru hattı bunları denetler.</summary>
    public static IReadOnlySet<string> ExtraRequired(FormLayoutInfo layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Fields.Where(f => f.Required && !f.DefaultRequired).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static List<FormFieldLayout> DefaultFields(FormDefinition definition, IReadOnlyList<CustomFieldInfo> customFields)
    {
        var fields = definition.Fields
            .Select(f => new FormFieldLayout(f.Key, f.Label, f.Section, f.Span, f.Required, Visible: true, f.Fixed, IsCustom: false, DefaultRequired: f.Required))
            .ToList();
        fields.AddRange(customFields.Select(c => new FormFieldLayout(
            CustomKey(c.Key), c.Label, FormDefinition.ExtrasSection,
            c.Type is CustomFieldType.MultilineText or CustomFieldType.MultiChoice ? 12 : 6,
            c.Required, Visible: true, Fixed: false, IsCustom: true, DefaultRequired: c.Required)));
        return fields;
    }

    private static bool IsValidKey(string? key) => key is { Length: > 0 and <= KeyMaxLength } && KeyPattern().IsMatch(key);

    private static bool IsValidLabel(string? label) => !string.IsNullOrWhiteSpace(label) && label.Trim().Length <= LabelMaxLength;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex KeyPattern();
}
