using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Modules;

/// <summary>Form düzeni tanımları (ADR-0021): modül başına sabit tanım; açılışta doğrulanır.</summary>
public sealed partial class ModuleCatalog
{
    private readonly Dictionary<string, (string ModuleKey, FormDefinition Form)> forms = new(StringComparer.Ordinal);

    /// <summary>Form tanımı ve modülü; tanımlı değilse null.</summary>
    public (string ModuleKey, FormDefinition Form)? FindForm(string entityKey) => forms.TryGetValue(entityKey, out var form) ? form : null;

    /// <summary>Form tanımları, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, FormDefinition Form)> Forms => Modules.SelectMany(m => m.Forms.Select(f => (m.Key, f)));

    private void RegisterForms(IModule module)
    {
        foreach (var form in module.Forms)
        {
            ValidateForm(module, form);
            if (!forms.TryAdd(form.EntityKey, (module.Key, form)))
            {
                throw new InvalidOperationException($"Form düzeni iki kez tanımlı: '{form.EntityKey}'.");
            }
        }
    }

    private static void ValidateForm(IModule module, FormDefinition form)
    {
        bool Fails(string what) => throw new InvalidOperationException($"Geçersiz form düzeni tanımı '{form.EntityKey}' ({module.Key}): {what}.");

        if (!form.EntityKey.StartsWith(module.Key + ".", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(form.DisplayName))
        {
            Fails("kayıt türü modülün olmalı, görünen ad verilmeli");
        }

        if (form.Tabs.Count == 0 || form.Tabs[0].Key != FormDefinition.DefaultTab
            || form.Tabs.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != form.Tabs.Count
            || form.Tabs.Any(t => string.IsNullOrWhiteSpace(t.Label)))
        {
            Fails($"ilk sekme '{FormDefinition.DefaultTab}' olmalı, sekme anahtarları tekil ve adları dolu olmalı");
        }

        if (form.Sections.Count == 0
            || form.Sections.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != form.Sections.Count
            || form.Sections.All(s => s.Key != FormDefinition.ExtrasSection)
            || form.Sections.Any(s => string.IsNullOrWhiteSpace(s.Label) || form.Tabs.All(t => t.Key != s.Tab)))
        {
            Fails($"bölüm anahtarları tekil olmalı, '{FormDefinition.ExtrasSection}' bölümü bulunmalı ve her bölüm bir sekmeye bağlı olmalı");
        }

        if (form.Fields.Count == 0 || form.Fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != form.Fields.Count)
        {
            Fails("alan anahtarları tekil olmalı ve en az bir alan bulunmalı");
        }

        foreach (var field in form.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || field.Key.StartsWith("custom.", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(field.Label) || !FormSpans.Allowed.Contains(field.Span)
                || form.Sections.All(s => s.Key != field.Section))
            {
                Fails($"alan '{field.Key}'");
            }
        }
    }
}
