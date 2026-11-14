using Modulix.Core.FieldRules;
using Modulix.Core.Modules;

namespace Modulix.Core.Customization;

/// <summary>
/// Firmanın özelleştirme sınırları (ADR-0041 K7). Varsayılan hepsi sınırsızdır (<c>null</c>); sınır gerekirse yapılandırmadan
/// (<c>Customization:Limits</c>) verilir, kod değişmez.
/// </summary>
public sealed class CustomizationLimits
{
    /// <summary>Bir kayıt türündeki etkin özel alan sayısı.</summary>
    public int? MaxFieldsPerEntity { get; set; }

    /// <summary>Bir özel alanın seçenek sayısı.</summary>
    public int? MaxOptionsPerField { get; set; }

    /// <summary>Bir seçim listesindeki değer sayısı.</summary>
    public int? MaxValuesPerList { get; set; }

    public int? MaxSubProfiles { get; set; }

    public int? MaxOptionsPerSubProfile { get; set; }
}

/// <summary>Alt profil kuralları (saf): girdinin doğrulanması ve uygulanacak şeylerin <see cref="SectorProfile"/> biçimine çevrilmesi.</summary>
public static class SubProfileRules
{
    /// <summary>Girdiyi doğrular ve normalleştirir (ad kırpılır, gereksiz alanlar temizlenir). Hatalar <see cref="ArgumentException"/>.</summary>
    public static SubProfileOptionInput Normalize(SubProfileOptionInput input, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(modules);
        var label = input.Label?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(input.Kind))
        {
            throw new ArgumentException("Geçersiz seçenek türü.", "kind");
        }

        if (label.Length == 0 || label.Length > (input.Kind == SubProfileOptionKind.Field ? CustomField.LabelMaxLength : ChoiceListRules.LabelMaxLength))
        {
            throw new ArgumentException("Ad zorunludur ve çok uzun olamaz.", "label");
        }

        if (input.Kind == SubProfileOptionKind.ListValue)
        {
            if (modules.FindChoiceList(input.TargetKey) is not { } list)
            {
                throw new ArgumentException("Bu liste tanımlı değil.", "targetKey");
            }

            var category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category;
            if (list.List.Categories.Count == 0 ? category is not null : list.List.Categories.All(c => c.Key != category))
            {
                throw new ArgumentException("Bu liste için geçerli bir kategori seçin.", "category");
            }

            return new SubProfileOptionInput(input.Kind, input.TargetKey, label, null, false, false, null, category);
        }

        if (modules.FindCustomizable(input.TargetKey) is null)
        {
            throw new ArgumentException("Bu kayıt türüne özel alan eklenemez.", "targetKey");
        }

        if (input.FieldType is not { } type || !Enum.IsDefined(type))
        {
            throw new ArgumentException("Alan tipi zorunludur.", "fieldType");
        }

        var choices = (input.Choices ?? []).Select(c => c?.Trim() ?? string.Empty).ToList();
        var hasChoices = type is CustomFieldType.Choice or CustomFieldType.MultiChoice;
        if (hasChoices != (choices.Count > 0))
        {
            throw new ArgumentException(hasChoices ? "Seçim tipli alan için en az bir seçenek verin." : "Yalnızca seçim tipli alanın seçeneği olur.", "choices");
        }

        if (choices.Any(c => c.Length == 0 || c.Length > CustomField.LabelMaxLength)
            || choices.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != choices.Count)
        {
            throw new ArgumentException("Seçenek adları boş, çok uzun ya da tekrarlı olamaz.", "choices");
        }

        return new SubProfileOptionInput(input.Kind, input.TargetKey, label, type, input.Required, input.ShowInList, choices, null);
    }

    /// <summary>Hizmet özelliği girdisini kırpar ve doğrular. Hatalar <see cref="ArgumentException"/> (ParamName: group ya da value).</summary>
    public static SubProfileFeatureInput NormalizeFeature(SubProfileFeatureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var group = input.Group?.Trim() ?? string.Empty;
        var value = input.Value?.Trim() ?? string.Empty;
        if (group.Length == 0 || group.Length > SubProfileFeature.GroupMaxLength)
        {
            throw new ArgumentException($"Özellik adı zorunludur ve en çok {SubProfileFeature.GroupMaxLength} karakter olabilir.", "group");
        }

        if (value.Length == 0 || value.Length > SubProfileFeature.ValueMaxLength)
        {
            throw new ArgumentException($"Özellik değeri zorunludur ve en çok {SubProfileFeature.ValueMaxLength} karakter olabilir.", "value");
        }

        return new SubProfileFeatureInput(group, value);
    }

    /// <summary>Aynı grup ve değer iki kez olamaz (büyük/küçük harf fark etmez).</summary>
    public static bool IsDuplicateFeature(SubProfileFeatureInput input, IEnumerable<SubProfileFeature> others) =>
        others.Any(f => string.Equals(f.Group, input.Group, StringComparison.CurrentCultureIgnoreCase)
            && string.Equals(f.Value, input.Value, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Özellikleri grup adıyla, ilk görülme sırasıyla kümeler ("Taşıma türü: Komple, Parsiyel").</summary>
    public static IReadOnlyList<string> GroupLabels(IEnumerable<SubProfileFeatureInput> features) =>
        [.. features.GroupBy(f => f.Group, StringComparer.CurrentCultureIgnoreCase).Select(g => $"{g.First().Group}: {string.Join(", ", g.Select(f => f.Value))}")];

    /// <summary>Kural girdisini kırpar ve doğrular (ad zorunlu, kural gerekli). Alan, etki ve koşul doğrulaması servisin işidir (firmanın alan kümesi gerekir). Hatalar <see cref="ArgumentException"/>.</summary>
    public static SubProfileRuleInput NormalizeRule(SubProfileRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > SubProfileRule.NameMaxLength)
        {
            throw new ArgumentException($"Kural adı zorunludur ve en çok {SubProfileRule.NameMaxLength} karakter olabilir.", "name");
        }

        return new SubProfileRuleInput(name, input.Rule ?? throw new ArgumentException("Kural gerekli.", "rule"));
    }

    /// <summary>
    /// Alt profil kuralının anahtarı: <c>sp.&lt;alt-profil-kodu&gt;.&lt;ad&gt;</c> (ADR-0041 K4). Alt çizgi kural anahtarında geçerli olmadığından kod ve ad
    /// tire ile yazılır; <paramref name="taken"/> içindeyse sonuna sayı eklenir. Öneki sabit olduğundan alt profil kuralı ürün ya da sektör kuralının anahtarıyla çakışmaz.
    /// </summary>
    public static string RuleKey(string subProfileCode, string name, IReadOnlySet<string> taken)
    {
        var prefix = $"sp.{subProfileCode.Replace('_', '-')}.";
        var slug = CustomFieldRules.KeyFrom(name, new HashSet<string>(), FieldRule.KeyMaxLength - prefix.Length - 4).Replace('_', '-');
        var key = prefix + slug;
        var candidate = key;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{key}-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }


    /// <summary>Aynı alt profilde aynı hedefte aynı ad iki kez olamaz (büyük/küçük harf fark etmez).</summary>
    public static bool IsDuplicate(SubProfileOptionInput input, IEnumerable<SubProfileOption> others) =>
        others.Any(o => o.Kind == input.Kind
            && string.Equals(o.TargetKey, input.TargetKey, StringComparison.Ordinal)
            && string.Equals(o.Label, input.Label, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// Uygulanmamış seçenekleri <see cref="SectorProfileRules.Plan"/>'ın anladığı profile çevirir; böylece "yalnızca ekler, aynı adlı atlanır, kapalı modül
    /// uygulanmaz" kuralı tek yerde kalır. Modüller seçeneklerin hedeflerinden türer.
    /// </summary>
    public static SectorProfile ToProfile(SubProfile subProfile, IEnumerable<SubProfileOption> options, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(subProfile);
        var pending = options.Where(o => !o.IsApplied).OrderBy(o => o.SortOrder).ToList();
        var fields = new List<ProfileField>();
        var values = new List<ProfileListValue>();
        var moduleKeys = new List<string>();
        foreach (var option in pending)
        {
            if (option.Kind == SubProfileOptionKind.Field)
            {
                if (modules.FindCustomizable(option.TargetKey) is { } entity && option.FieldType is { } type)
                {
                    fields.Add(new ProfileField(option.TargetKey, option.Label, type, option.Required, option.ShowInList, option.Choices.Count == 0 ? null : option.Choices));
                    moduleKeys.Add(entity.ModuleKey);
                }
            }
            else if (modules.FindChoiceList(option.TargetKey) is { } list)
            {
                values.Add(new ProfileListValue(option.TargetKey, option.Label, option.Category));
                moduleKeys.Add(list.ModuleKey);
            }
        }

        return new SectorProfile(
            subProfile.Code,
            subProfile.Name,
            subProfile.Description ?? string.Empty,
            [.. moduleKeys.Where(m => !ModuleCatalog.IsAlwaysEnabled(m)).Distinct(StringComparer.Ordinal)],
            fields,
            values);
    }
}
