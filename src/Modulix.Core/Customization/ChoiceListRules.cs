using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Customization;

/// <summary>Listede istenen değer, istenen sırayla. Anahtarı olmayan değer yenidir; kategorisi yalnızca yeni değerde seçilir.</summary>
public sealed record ChoiceValueInput(string? Key, string Label, string? Category = null, bool Active = true);

/// <summary>Seçim listelerinin (Y9b) kuralları: varsayılanla birleştirme ve düzenlemenin doğrulanması.</summary>
public static class ChoiceListRules
{
    public const int LabelMaxLength = 60;
    public const int MaxValues = 50;

    /// <summary>
    /// Firmanın listesi: saklanan değerler sırasıyla, ardından kodda sonradan eklenmiş hazır değerler. Hazır değerin
    /// kategorisi her zaman koddakidir; saklanan tanımsız kategori yok sayılır.
    /// </summary>
    public static ChoiceList Resolve(ChoiceListDefinition definition, IReadOnlyList<StoredChoiceValue>? stored)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var builtIn = definition.Values.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var categories = definition.Categories.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var values = new List<ChoiceValue>();
        foreach (var value in stored ?? [])
        {
            if (values.Any(v => v.Key == value.Key) || !ChoiceValueKeys.IsValid(value.Key))
            {
                continue;
            }

            var definitionValue = builtIn.GetValueOrDefault(value.Key);
            var category = definitionValue?.Category ?? (value.Category is { } c && categories.Contains(c) ? c : null);
            values.Add(new ChoiceValue(value.Key, value.Label, category, value.Active, definitionValue is not null));
        }

        values.AddRange(definition.Values
            .Where(d => values.All(v => v.Key != d.Key))
            .Select(d => new ChoiceValue(d.Key, d.Label, d.Category, Active: true, BuiltIn: true)));
        return new ChoiceList(definition.Key, values, definition.Default);
    }

    /// <summary>
    /// Düzenlemeyi uygular. Listede verilmeyen değer silinmez, pasif olarak sona eklenir (kayıtlarda anahtarı olabilir).
    /// Hatalar <see cref="ArgumentException"/> ("values") olarak döner.
    /// </summary>
    public static IReadOnlyList<StoredChoiceValue> Apply(ChoiceListDefinition definition, ChoiceList current, IReadOnlyList<ChoiceValueInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(inputs);

        var categories = definition.Categories.ToDictionary(c => c.Key, StringComparer.Ordinal);
        // Veritabanı karşılaştırması büyük/küçük harfe duyarsızdır: "lead" ile "Lead" ayrı anahtar olamaz.
        var taken = current.Values.Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new List<StoredChoiceValue>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Trim().Length > LabelMaxLength)
            {
                throw new ArgumentException($"Değer adı zorunludur ve en fazla {LabelMaxLength} karakter olabilir.", "values");
            }

            var label = input.Label.Trim();
            if (values.Any(v => string.Equals(v.Label, label, StringComparison.CurrentCultureIgnoreCase)))
            {
                throw new ArgumentException($"Değer iki kez verildi: '{label}'.", "values");
            }

            if (input.Key is { } key)
            {
                if (current.Find(key) is not { } existing || values.Any(v => v.Key == existing.Key))
                {
                    throw new ArgumentException("Bilinmeyen değer.", "values");
                }

                var existingCategory = existing.Category;
                if (input.Category is { } changed && changed != existing.Category)
                {
                    // Kategori tanımlanmadan önce eklenmiş özel değere bir kez kategori atanabilir; atandıktan sonra (ve hazır değerde) değişmez.
                    if (existing.Category is null && !existing.BuiltIn && categories.ContainsKey(changed))
                    {
                        existingCategory = changed;
                    }
                    else
                    {
                        throw new ArgumentException($"'{label}' değerinin kategorisi değiştirilemez.", "values");
                    }
                }

                values.Add(new StoredChoiceValue(existing.Key, label, existingCategory, input.Active));
                continue;
            }

            string? category = null;
            if (categories.Count > 0)
            {
                if (input.Category is not { } chosen || !categories.ContainsKey(chosen))
                {
                    throw new ArgumentException($"'{label}' için bir kategori seçin.", "values");
                }

                category = chosen;
            }

            var generated = CustomFieldRules.KeyFrom(label, taken, ChoiceValueKeys.MaxLength);
            taken.Add(generated);
            values.Add(new StoredChoiceValue(generated, label, category, input.Active));
        }

        values.AddRange(current.Values.Where(c => values.All(v => v.Key != c.Key)).Select(c => new StoredChoiceValue(c.Key, c.Label, c.Category, false)));
        if (values.Count > MaxValues)
        {
            throw new ArgumentException($"Bir listede en fazla {MaxValues} değer olabilir.", "values");
        }

        if (!values.Any(v => v.Active))
        {
            throw new ArgumentException("En az bir etkin değer olmalıdır.", "values");
        }

        // Kod kategoriye bakar (ör. kazanılan fırsat); her kategoride seçilebilecek bir değer kalmalı.
        if (definition.Categories.FirstOrDefault(c => !values.Any(v => v.Active && v.Category == c.Key)) is { } empty)
        {
            throw new ArgumentException($"'{empty.DisplayName}' kategorisinde en az bir etkin değer kalmalıdır.", "values");
        }

        return values;
    }
}
