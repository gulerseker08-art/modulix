using Modulix.Core.Contracts.Customization;
using Modulix.Core.Modules;

namespace Modulix.Core.Customization;

/// <summary>Profilin önerdiği özel alan (Y9a tanımıyla aynı); seçenekler adlarıyla verilir, anahtarları üretilir.</summary>
public sealed record ProfileField(
    string EntityKey,
    string Label,
    CustomFieldType Type,
    bool Required = false,
    bool ShowInList = false,
    IReadOnlyList<string>? Options = null);

/// <summary>Profilin bir listeye eklediği değer (Y9b); listenin kategorisi varsa <see cref="Category"/> verilir.</summary>
public sealed record ProfileListValue(string ListKey, string Label, string? Category = null);

/// <summary>
/// Sektör profili (Y9c): bir sektör için hazır başlangıç paketi. Kodda tanımlıdır, firma verisi değildir. Firmaya bir kez
/// uygulanır ve yalnızca ekler (<see cref="SectorProfileRules.Plan"/>); firmanın mevcut alanları ve değerleri değişmez.
/// </summary>
public sealed record SectorProfile(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<string> Modules,
    IReadOnlyList<ProfileField> Fields,
    IReadOnlyList<ProfileListValue> ListValues);

/// <summary>Profiller; açılışta tanımlar modül kataloğuna göre doğrulanır (bilinmeyen modül, kayıt türü, liste, kategori).</summary>
public sealed class SectorProfileCatalog
{
    public const int KeyMaxLength = 50;

    public SectorProfileCatalog(IEnumerable<SectorProfile> profiles, ModuleCatalog modules)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(modules);
        Profiles = [.. profiles];
        foreach (var profile in Profiles)
        {
            Validate(profile, modules);
        }

        if (Profiles.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != Profiles.Count)
        {
            throw new InvalidOperationException("Sektör profili anahtarları tekil olmalıdır.");
        }
    }

    public IReadOnlyList<SectorProfile> Profiles { get; }

    public SectorProfile? Find(string? key) => Profiles.FirstOrDefault(p => p.Key == key);

    private static void Validate(SectorProfile profile, ModuleCatalog modules)
    {
        bool Fails(string what) => throw new InvalidOperationException($"Geçersiz sektör profili '{profile.Key}': {what}.");

        if (string.IsNullOrWhiteSpace(profile.Key) || profile.Key.Length > KeyMaxLength || !profile.Key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            Fails("anahtar");
        }

        foreach (var module in profile.Modules)
        {
            if (!modules.ContainsModule(module) || ModuleCatalog.IsAlwaysEnabled(module))
            {
                Fails($"modül '{module}'");
            }
        }

        foreach (var field in profile.Fields)
        {
            var hasOptions = field.Type is CustomFieldType.Choice or CustomFieldType.MultiChoice;
            if (modules.FindCustomizable(field.EntityKey) is not { } entity
                || !profile.Modules.Contains(entity.ModuleKey)
                || string.IsNullOrWhiteSpace(field.Label)
                || field.Label.Length > CustomField.LabelMaxLength
                || hasOptions != (field.Options is { Count: > 0 }))
            {
                Fails($"alan '{field.Label}'");
            }
        }

        foreach (var value in profile.ListValues)
        {
            if (modules.FindChoiceList(value.ListKey) is not { } list
                || !profile.Modules.Contains(list.ModuleKey)
                || string.IsNullOrWhiteSpace(value.Label)
                || value.Label.Length > ChoiceListRules.LabelMaxLength
                || (list.List.Categories.Count == 0 ? value.Category is not null : list.List.Categories.All(c => c.Key != value.Category)))
            {
                Fails($"liste değeri '{value.Label}'");
            }
        }
    }
}

/// <summary>
/// Profilin firmaya ekleyeceği şeyler. <see cref="ModulesToEnable"/> açılacak modüller (yalnızca platform yöneticisi açar);
/// <see cref="UnavailableModules"/> kapalı kalan ve bu yüzden alanları ve değerleri eklenmeyen modüller.
/// </summary>
public sealed record ProfilePlan(
    SectorProfile Profile,
    IReadOnlyList<string> ModulesToEnable,
    IReadOnlyList<string> UnavailableModules,
    IReadOnlyList<ProfileField> Fields,
    IReadOnlyList<ProfileListValue> ListValues)
{
    public bool IsEmpty => ModulesToEnable.Count == 0 && Fields.Count == 0 && ListValues.Count == 0;
}

/// <summary>Profili uygulamanın kuralları: yalnızca eksikleri ekler; aynı adlı alan ve değer (büyük/küçük harf fark etmez) atlanır.</summary>
public static class SectorProfileRules
{
    /// <param name="enabledModules">Firmada açık modüller.</param>
    /// <param name="canEnableModules">Kapalı modüller açılabilir mi (platform yöneticisi)?</param>
    /// <param name="fieldLabels">Kayıt türünün mevcut alan adları (arşivdekiler dahil: arşivlenen alan geri getirilmez).</param>
    /// <param name="lists">Firmanın listeleri (anahtar → liste).</param>
    public static ProfilePlan Plan(
        SectorProfile profile,
        ModuleCatalog modules,
        IReadOnlySet<string> enabledModules,
        bool canEnableModules,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fieldLabels,
        IReadOnlyDictionary<string, ChoiceList> lists)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(modules);
        var missing = profile.Modules.Where(m => !enabledModules.Contains(m)).ToList();
        var toEnable = canEnableModules ? missing : [];
        var unavailable = canEnableModules ? [] : missing;
        bool Usable(string? moduleKey) => moduleKey is not null && !unavailable.Contains(moduleKey);

        var fields = profile.Fields
            .Where(f => Usable(modules.FindCustomizable(f.EntityKey)?.ModuleKey))
            .Where(f => !(fieldLabels.GetValueOrDefault(f.EntityKey) ?? []).Any(l => SameLabel(l, f.Label)))
            .ToList();
        var values = profile.ListValues
            .Where(v => Usable(modules.FindChoiceList(v.ListKey)?.ModuleKey))
            .Where(v => lists.GetValueOrDefault(v.ListKey) is not { } list || !list.Values.Any(c => SameLabel(c.Label, v.Label)))
            .ToList();
        return new ProfilePlan(profile, toEnable, unavailable, fields, values);
    }

    /// <summary>
    /// Listeye eklenecek değerlerle düzenleme girdisi: mevcut değerler olduğu gibi, yeni değer kendi kategorisinin son
    /// değerinin arkasına (kategori yoksa sona).
    /// </summary>
    public static IReadOnlyList<ChoiceValueInput> Extend(ChoiceList list, IEnumerable<ProfileListValue> additions)
    {
        ArgumentNullException.ThrowIfNull(list);
        var inputs = list.Values.Select(v => (Input: new ChoiceValueInput(v.Key, v.Label, null, v.Active), v.Category)).ToList();
        foreach (var value in additions)
        {
            var last = inputs.FindLastIndex(i => value.Category is not null && i.Category == value.Category);
            var entry = (new ChoiceValueInput(null, value.Label, value.Category), value.Category);
            if (last < 0)
            {
                inputs.Add(entry);
            }
            else
            {
                inputs.Insert(last + 1, entry);
            }
        }

        return [.. inputs.Select(i => i.Input)];
    }

    private static bool SameLabel(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>Firmanın profili: en son uygulanan profil ve zamanı.</summary>
public sealed record TenantProfile(string? ProfileKey, DateTimeOffset? AppliedAt);

/// <summary>Sektör profillerinin firmaya uygulanması (Y9c), aktif tenant için.</summary>
public interface ISectorProfileService
{
    Task<TenantProfile> GetAsync(CancellationToken cancellationToken);

    /// <summary>Tanımsız profil için null.</summary>
    Task<ProfilePlan?> PreviewAsync(string profileKey, bool enableModules, CancellationToken cancellationToken);

    /// <summary>
    /// Planı uygular ve profili firmaya yazar; tek işlemdir. <paramref name="enableModules"/> yalnızca platform yöneticisi
    /// içindir. Profil yeniden uygulanabilir: yalnızca o gün eksik olanlar eklenir. Tanımsız profil için null.
    /// </summary>
    Task<ProfilePlan?> ApplyAsync(string profileKey, bool enableModules, CancellationToken cancellationToken);
}
