using Modulix.Core.Modules;

namespace Modulix.Core.Customization;

/// <summary>
/// Hazır alt profil (ADR-0041 K9): firmanın tek tıkla kendi alt profili olarak ekleyebildiği öneri. Kodda tanımlıdır, firma verisi değildir;
/// eklenince firmanın alt profili (taslak) olur ve firma istediği gibi değiştirir. İçerik ürün önerisidir, bir işletmeyle doğrulanmamıştır.
/// <see cref="Key"/> eklenen alt profilin koduna dönüşür; <see cref="ActivityCode"/> katalogda yoksa ya da pasifse bağlanmadan eklenir.
/// </summary>
public sealed record SubProfileStarter(
    string Key,
    string Name,
    string Description,
    string? ActivityCode,
    IReadOnlyList<SubProfileOptionInput> Options,
    IReadOnlyList<SubProfileFeatureInput>? Features = null);

/// <summary>Hazır alt profiller; açılışta tanımlar modül kataloğuna göre doğrulanır (bilinmeyen kayıt türü, liste, kategori, tekrar eden seçenek).</summary>
public sealed class SubProfileStarterCatalog
{
    private readonly Dictionary<string, IReadOnlyList<string>> modulesByKey;

    public SubProfileStarterCatalog(IEnumerable<SubProfileStarter> starters, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(starters);
        ArgumentNullException.ThrowIfNull(modules);
        Starters = [.. starters];
        modulesByKey = [];
        foreach (var starter in Starters)
        {
            modulesByKey[starter.Key] = Validate(starter, modules);
        }

        if (Starters.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != Starters.Count)
        {
            throw new InvalidOperationException("Hazır alt profil anahtarları tekil olmalıdır.");
        }
    }

    public IReadOnlyList<SubProfileStarter> Starters { get; }

    public SubProfileStarter? Find(string? key) => Starters.FirstOrDefault(s => s.Key == key);

    /// <summary>Seçeneklerin hedeflerinden türeyen, alt profilin çalışması için firmada açık olması gereken modüller.</summary>
    public IReadOnlyList<string> ModulesOf(SubProfileStarter starter) => modulesByKey.GetValueOrDefault(starter.Key) ?? [];

    private static IReadOnlyList<string> Validate(SubProfileStarter starter, ModuleCatalog modules)
    {
        InvalidOperationException Fails(string what) => new($"Geçersiz hazır alt profil '{starter.Key}': {what}.");

        if (string.IsNullOrWhiteSpace(starter.Key) || starter.Key.Length > SubProfile.CodeMaxLength
            || !starter.Key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
        {
            throw Fails("anahtar");
        }

        if (string.IsNullOrWhiteSpace(starter.Name) || starter.Name.Trim().Length > SubProfile.NameMaxLength
            || starter.Description?.Length > SubProfile.DescriptionMaxLength)
        {
            throw Fails("ad ya da açıklama");
        }

        if (starter.Options.Count == 0)
        {
            throw Fails("seçenek yok");
        }

        var seenFeatures = new List<SubProfileFeature>();
        foreach (var feature in starter.Features ?? [])
        {
            SubProfileFeatureInput normalizedFeature;
            try
            {
                normalizedFeature = SubProfileRules.NormalizeFeature(feature);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"Geçersiz hazır alt profil '{starter.Key}': özellik '{feature.Group}: {feature.Value}' ({exception.Message}).", exception);
            }

            if (SubProfileRules.IsDuplicateFeature(normalizedFeature, seenFeatures))
            {
                throw Fails($"özellik tekrar ediyor '{feature.Group}: {feature.Value}'");
            }

            seenFeatures.Add(new SubProfileFeature(Guid.Empty, normalizedFeature.Group, normalizedFeature.Value, 0));
        }

        var seen = new List<SubProfileOption>();
        var required = new List<string>();
        foreach (var option in starter.Options)
        {
            SubProfileOptionInput normalized;
            try
            {
                normalized = SubProfileRules.Normalize(option, modules);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException($"Geçersiz hazır alt profil '{starter.Key}': seçenek '{option.Label}' ({exception.Message}).", exception);
            }

            if (SubProfileRules.IsDuplicate(normalized, seen))
            {
                throw Fails($"seçenek tekrar ediyor '{option.Label}'");
            }

            seen.Add(new SubProfileOption(Guid.Empty, 0) { Kind = normalized.Kind, TargetKey = normalized.TargetKey, Label = normalized.Label });
            var module = normalized.Kind == SubProfileOptionKind.Field
                ? modules.FindCustomizable(normalized.TargetKey)?.ModuleKey
                : modules.FindChoiceList(normalized.TargetKey)?.ModuleKey;
            if (module is not null && !required.Contains(module))
            {
                required.Add(module);
            }
        }

        return required;
    }
}

/// <summary>Hazır alt profilin firma için görünümü: eklenmiş mi, hangi modüller kapalı olduğu için eklenemiyor.</summary>
public sealed record SubProfileStarterView(
    string Key,
    string Name,
    string Description,
    string? ActivityName,
    IReadOnlyList<string> OptionLabels,
    IReadOnlyList<string> FeatureLabels,
    IReadOnlyList<string> MissingModules,
    bool Added);
