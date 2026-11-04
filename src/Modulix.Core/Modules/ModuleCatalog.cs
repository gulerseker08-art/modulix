using System.Text.RegularExpressions;
using Modulix.Core.Contracts.Customization;
using Modulix.Core.Contracts.Events;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.SharedRecords;
using Modulix.Core.Settings;

namespace Modulix.Core.Modules;

/// <summary>
/// Uygulamada kayıtlı modüller ve izinleri. Açılışta bir kez oluşturulur; anahtar biçimi,
/// tekrar eden modül veya izin, modül anahtarıyla başlamayan izin varsa uygulama açılmaz.
/// </summary>
public sealed partial class ModuleCatalog
{
    /// <summary>Abonelik anahtarının en fazla uzunluğu (teslim satırında saklanır).</summary>
    public const int SubscriptionKeyMaxLength = 100;

    private readonly Dictionary<string, IModule> modules;
    private readonly Dictionary<string, PermissionDefinition> permissions;
    private readonly Dictionary<string, AuditedEntity> auditedEntities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, SettingDefinition Definition)> settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SensitiveFields>> sensitiveFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, CustomizableEntity Entity)> customizable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, ChoiceListDefinition List)> choiceLists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, LookupDefinition Lookup)> lookups = new(StringComparer.Ordinal);
    private readonly List<SharedRecordAccess> sharedAccess = [];
    private readonly Dictionary<string, (string ModuleKey, EventSubscription Subscription)> subscriptions = new(StringComparer.Ordinal);

    public ModuleCatalog(IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        this.modules = new Dictionary<string, IModule>(StringComparer.Ordinal);
        permissions = new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (!AccessKeys.IsValidModuleKey(module.Key))
            {
                throw new InvalidOperationException(
                    $"Geçersiz modül anahtarı: '{module.Key}' ({module.GetType().Name}). Küçük harf ve rakam, 2-{AccessKeys.ModuleKeyMaxLength} karakter.");
            }

            if (!this.modules.TryAdd(module.Key, module))
            {
                throw new InvalidOperationException($"Modül anahtarı iki kez kayıtlı: '{module.Key}'.");
            }

            foreach (var permission in module.Permissions)
            {
                if (!AccessKeys.IsValidPermissionKey(permission.Key)
                    || AccessKeys.ModuleOf(permission.Key) != module.Key)
                {
                    throw new InvalidOperationException(
                        $"Geçersiz izin anahtarı: '{permission.Key}' ({module.Key}). Biçim: '{module.Key}.kaynak.işlem'.");
                }

                if (!permissions.TryAdd(permission.Key, permission))
                {
                    throw new InvalidOperationException($"İzin iki kez tanımlı: '{permission.Key}'.");
                }
            }

            ValidateTemplates(module);

            foreach (var entity in module.AuditedEntities)
            {
                if (!entity.EntityKey.StartsWith(module.Key + ".", StringComparison.Ordinal) || !auditedEntities.TryAdd(entity.EntityKey, entity))
                {
                    throw new InvalidOperationException(
                        $"Geçersiz veya tekrar eden denetim kayıt türü: '{entity.EntityKey}' ({module.Key}). Biçim: '{module.Key}.tür'.");
                }
            }

            foreach (var sensitive in module.SensitiveFields)
            {
                if (!sensitive.EntityKey.StartsWith(module.Key + ".", StringComparison.Ordinal)
                    || !module.Permissions.Any(p => p.Key == sensitive.Permission)
                    || sensitive.Fields.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Geçersiz hassas alan tanımı: '{sensitive.EntityKey}' ({module.Key}). Kayıt türü ve izin modülün olmalı, en az bir alan verilmeli.");
                }

                (sensitiveFields.TryGetValue(sensitive.EntityKey, out var list) ? list : sensitiveFields[sensitive.EntityKey] = []).Add(sensitive);
            }

            foreach (var entity in module.CustomizableEntities)
            {
                if (!entity.EntityKey.StartsWith(module.Key + ".", StringComparison.Ordinal) || !customizable.TryAdd(entity.EntityKey, (module.Key, entity)))
                {
                    throw new InvalidOperationException(
                        $"Geçersiz veya tekrar eden özelleştirilebilir kayıt türü: '{entity.EntityKey}' ({module.Key}).");
                }
            }

            foreach (var list in module.ChoiceLists)
            {
                ValidateChoiceList(module.Key, list);
                if (!choiceLists.TryAdd(list.Key, (module.Key, list)))
                {
                    throw new InvalidOperationException($"Seçim listesi iki kez tanımlı: '{list.Key}'.");
                }
            }

            foreach (var lookup in module.Lookups)
            {
                ValidateLookup(module, lookup);
                if (!lookups.TryAdd(lookup.Key, (module.Key, lookup)))
                {
                    throw new InvalidOperationException($"Lookup iki kez tanımlı: '{lookup.Key}'.");
                }
            }

            RegisterForms(module);

            foreach (var access in module.SharedRecordAccess)
            {
                if (!SharedEntities.All.Contains(access.EntityKey)
                    || !module.Permissions.Any(p => p.Key == access.ReadPermission)
                    || (access.WritePermission is { } write && !module.Permissions.Any(p => p.Key == write)))
                {
                    throw new InvalidOperationException(
                        $"Geçersiz ortak kayıt erişimi: '{access.EntityKey}' ({module.Key}). Kayıt türü ortak kayıt, izinler modülün olmalı.");
                }

                sharedAccess.Add(access);
            }

            foreach (var reference in module.SharedRecordReferences)
            {
                if (!SharedEntities.All.Contains(reference.SharedEntityKey)
                    || !reference.EntityKey.StartsWith(module.Key + ".", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(reference.Field))
                {
                    throw new InvalidOperationException(
                        $"Geçersiz ortak kayıt referansı: '{reference.EntityKey}.{reference.Field}' ({module.Key}).");
                }
            }

            foreach (var subscription in module.EventSubscriptions)
            {
                ValidateSubscription(module, subscription);
                if (!subscriptions.TryAdd(subscription.Key, (module.Key, subscription)))
                {
                    throw new InvalidOperationException($"Olay aboneliği iki kez tanımlı: '{subscription.Key}'.");
                }
            }

            foreach (var setting in module.Settings)
            {
                ValidateSetting(module.Key, setting);
                if (!settings.TryAdd(setting.Key, (module.Key, setting)))
                {
                    throw new InvalidOperationException($"Ayar iki kez tanımlı: '{setting.Key}'.");
                }
            }
        }

        // Denetim kaydında ayarın adı gösterilir (ör. "core.regional.currency" yerine "Para birimi").
        if (auditedEntities.TryGetValue(CoreModule.AuditedEntityKeys.TenantSetting, out var settingEntity))
        {
            auditedEntities[settingEntity.EntityKey] = settingEntity with
            {
                Options = new Dictionary<string, IReadOnlyDictionary<string, string>>(settingEntity.Options)
                {
                    ["key"] = settings.Values.ToDictionary(s => s.Definition.Key, s => s.Definition.DisplayName, StringComparer.Ordinal),
                },
            };
        }

        if (!this.modules.ContainsKey(CoreModule.ModuleKey))
        {
            throw new InvalidOperationException($"Katalogda '{CoreModule.ModuleKey}' modülü yok.");
        }

        // Atama e-postası modüllerin sorumlu atanan kayıt türlerine gider (ADR-0007); yeni modül ek iş yapmadan katılır.
        // Sorumlu atanan tür yoksa abonelik de yoktur (boş liste "bütün türler" demektir).
        if (subscriptions.TryGetValue(CoreModule.Subscriptions.AssignmentEmail, out var assignment))
        {
            var owned = this.modules.Values.SelectMany(m => m.OwnedRecords).Select(t => t.EntityKey).Distinct().Order(StringComparer.Ordinal).ToList();
            if (owned.Count == 0)
            {
                subscriptions.Remove(assignment.Subscription.Key);
            }
            else
            {
                subscriptions[assignment.Subscription.Key] = (assignment.ModuleKey, assignment.Subscription with { EntityKeys = owned });
            }
        }

        // Abonelikler başka modüllerin kayıt türlerine bağlanabilir; türler bütün modüller okunduktan sonra denetlenir.
        foreach (var (moduleKey, subscription) in subscriptions.Values)
        {
            var unknown = subscription.EntityKeys.Where(k => !auditedEntities.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"'{subscription.Key}' aboneliğinde bilinmeyen kayıt türü: {string.Join(", ", unknown)} ({moduleKey}).");
            }
        }

        Modules = [.. this.modules.Values.OrderBy(m => m.Key, StringComparer.Ordinal)];

        // Kural yetenekleri izinlere, modüllere ve lookup'lara başvurur; hepsi okunduktan sonra doğrulanır (ADR-0027).
        RegisterAutomation();

        // Rapor veri kümeleri seçim listelerine, lookup'lara ve özelleştirilebilir kayıt türlerine başvurur (ADR-0026, dilim 3).
        RegisterReporting();
    }

    /// <summary>"Salt okur" şablonu: açık modüllerin tüm görüntüleme izinleri; çekirdek tanımlar.</summary>
    public const string ReadOnlyTemplateKey = "core.read-only";

    /// <summary>Core modülü ve verilen modüllerden katalog oluşturur.</summary>
    public static ModuleCatalog Create(params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return new ModuleCatalog([new CoreModule(), .. modules]);
    }

    /// <summary>Anahtara göre sıralı.</summary>
    public IReadOnlyList<IModule> Modules { get; }

    public IModule? Find(string moduleKey) => modules.GetValueOrDefault(moduleKey);

    public bool ContainsModule(string moduleKey) => modules.ContainsKey(moduleKey);

    /// <summary>Denetim kaydında gösterilen kayıt türü (Y5); tanımlı değilse <c>null</c>.</summary>
    public AuditedEntity? FindAuditedEntity(string entityKey) => auditedEntities.GetValueOrDefault(entityKey);

    /// <summary>Tüm denetim kayıt türleri, modül sırasıyla.</summary>
    public IEnumerable<AuditedEntity> AuditedEntities =>
        Modules.SelectMany(m => m.AuditedEntities).Select(e => auditedEntities[e.EntityKey]);

    /// <summary>Firma ayarının tanımı ve modülü (Y6); tanımlı değilse <c>null</c>.</summary>
    public (string ModuleKey, SettingDefinition Definition)? FindSetting(string key) =>
        settings.TryGetValue(key, out var setting) ? setting : null;

    /// <summary>Tüm firma ayarları, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, SettingDefinition Definition)> Settings =>
        Modules.SelectMany(m => m.Settings.Select(s => (m.Key, s)));

    public bool ContainsPermission(string permissionKey) => permissions.ContainsKey(permissionKey);

    /// <summary>
    /// Kayıt türünün, verilen geçerli izinlerle görülemeyen hassas alanları (Y8b). Yönetici tüm izinlere sahip olduğu için
    /// onun için boştur; çağıran yöneticinin izin kümesini (tüm izinler) verir.
    /// </summary>
    public IReadOnlySet<string> HiddenFields(string entityKey, IReadOnlySet<string> grantedPermissions) =>
        sensitiveFields.TryGetValue(entityKey, out var list)
            ? list.Where(s => !grantedPermissions.Contains(s.Permission)).SelectMany(s => s.Fields).ToHashSet(StringComparer.Ordinal)
            : NoFields;

    private static readonly IReadOnlySet<string> NoFields = new HashSet<string>();

    /// <summary>Özel alan eklenebilen kayıt türü ve modülü (Y9a); tanımlı değilse null.</summary>
    public (string ModuleKey, CustomizableEntity Entity)? FindCustomizable(string entityKey) =>
        customizable.TryGetValue(entityKey, out var entity) ? entity : null;

    /// <summary>Özel alan eklenebilen kayıt türleri, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, CustomizableEntity Entity)> CustomizableEntities =>
        Modules.SelectMany(m => m.CustomizableEntities.Select(e => (m.Key, e)));

    /// <summary>Düzenlenebilir seçim listesi ve modülü (Y9b); tanımlı değilse null.</summary>
    public (string ModuleKey, ChoiceListDefinition List)? FindChoiceList(string listKey) =>
        choiceLists.TryGetValue(listKey, out var list) ? list : null;

    /// <summary>Seçim listeleri, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, ChoiceListDefinition List)> ChoiceLists =>
        Modules.SelectMany(m => m.ChoiceLists.Select(l => (m.Key, l)));

    /// <summary>Lookup tanımı ve modülü (ADR-0016); tanımlı değilse null.</summary>
    public (string ModuleKey, LookupDefinition Lookup)? FindLookup(string key) => lookups.TryGetValue(key, out var lookup) ? lookup : null;

    /// <summary>Lookup tanımları, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, LookupDefinition Lookup)> Lookups => Modules.SelectMany(m => m.Lookups.Select(l => (m.Key, l)));

    /// <summary>Ortak kayıt türünü kullanan modüllerin bildirdiği izinler (ADR-0006).</summary>
    public IEnumerable<SharedRecordAccess> SharedAccessTo(string entityKey) => sharedAccess.Where(a => a.EntityKey == entityKey);

    /// <summary>Ortak kayıt türüne başvuran alanlar (ADR-0006), modül sırasıyla.</summary>
    public IEnumerable<SharedRecordReference> SharedReferencesTo(string entityKey) =>
        Modules.SelectMany(m => m.SharedRecordReferences).Where(r => r.SharedEntityKey == entityKey);

    /// <summary>Olay aboneliği ve modülü (ADR-0007); tanımlı değilse null.</summary>
    public (string ModuleKey, EventSubscription Subscription)? FindSubscription(string key) =>
        subscriptions.TryGetValue(key, out var subscription) ? subscription : null;

    /// <summary>Olayın gideceği abonelikler (tür ve kayıt türü eşleşen), anahtar sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, EventSubscription Subscription)> SubscriptionsFor(string eventType, string entityKey) =>
        subscriptions.Values.Where(s => s.Subscription.Matches(eventType, entityKey)).OrderBy(s => s.Subscription.Key, StringComparer.Ordinal);

    /// <summary>Tüm olay abonelikleri, modül ve tanım sırasıyla.</summary>
    public IEnumerable<(string ModuleKey, EventSubscription Subscription)> EventSubscriptions =>
        Modules.SelectMany(m => m.EventSubscriptions).Where(s => subscriptions.ContainsKey(s.Key)).Select(s => subscriptions[s.Key]);

    /// <summary>Kayıt türünün listeye bağlı alanlarının listeleri.</summary>
    public IEnumerable<ChoiceListDefinition> ChoiceListsOf(string entityKey) =>
        choiceLists.Values.Select(l => l.List).Where(l => l.EntityKey == entityKey);

    // Lookup anahtarı ve kaynak kayıt türü modülündür; üye lookup'ının hedefi yoktur, kayıt lookup'ının hedefi vardır; bağımlılık ya ikisiyle birlikte tanımlıdır ya hiç.
    private static void ValidateLookup(IModule module, LookupDefinition lookup)
    {
        var prefix = module.Key + ".";
        if (!lookup.Key.StartsWith(prefix, StringComparison.Ordinal)
            || !lookup.SourceEntityKey.StartsWith(prefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(lookup.Field)
            || string.IsNullOrWhiteSpace(lookup.Title)
            || lookup.IsMember != (lookup.TargetEntityKey is null)
            || (!lookup.IsMember && lookup.LabelFields.Count == 0)
            || (lookup.DependsOnField is null) != (lookup.DependsOnFilter is null)
            || lookup.PageSize is < 1 or > RecordLookupLimits.MaxPageSize
            || lookup.MinSearchLength is < 1 or > 10)
        {
            throw new InvalidOperationException($"Geçersiz lookup tanımı: '{lookup.Key}' ({module.Key}).");
        }
    }

    // Anahtarlar modülün; değer anahtarı harfle başlar (kayıtta ve süzgeçte metin olarak durur). Kategori varsa her
    // değer bilinen bir kategoriye bağlıdır ve her kategoride en az bir hazır değer vardır.
    private static void ValidateChoiceList(string moduleKey, ChoiceListDefinition list)
    {
        var prefix = moduleKey + ".";
        var keys = list.Values.Select(v => v.Key).ToList();
        var categories = list.Categories.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (!list.Key.StartsWith(prefix, StringComparison.Ordinal)
            || !list.EntityKey.StartsWith(prefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(list.Field)
            || keys.Count == 0
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count
            || !keys.All(ChoiceValueKeys.IsValid)
            || (list.Default is { } preferred && !keys.Contains(preferred))
            || categories.Count != list.Categories.Count
            || (categories.Count == 0
                ? list.Values.Any(v => v.Category is not null)
                : list.Values.Any(v => v.Category is null || !categories.Contains(v.Category))
                    || categories.Any(c => list.Values.All(v => v.Category != c))))
        {
            throw new InvalidOperationException($"Geçersiz seçim listesi tanımı: '{list.Key}' ({moduleKey}).");
        }
    }

    public PermissionDefinition? FindPermission(string permissionKey) => permissions.GetValueOrDefault(permissionKey);

    /// <summary>Katalogdaki tüm izin anahtarları; sistem rolü (<see cref="Authorization.Role.IsSystem"/>) bunları verir.</summary>
    public IEnumerable<string> PermissionKeys => permissions.Keys;

    /// <summary>Tenant entitlement'ından bağımsız olarak her zaman açık modüller (yalnızca Core).</summary>
    public static bool IsAlwaysEnabled(string moduleKey) => moduleKey == CoreModule.ModuleKey;

    /// <summary>
    /// Rollerin verdiği izinlerden geçerli olanlar: katalogda tanımlı ve modülü tenant için açık.
    /// Katalogdan kaldırılmış bir izin rolde kalmış olsa bile yok sayılır.
    /// </summary>
    public IReadOnlySet<string> EffectivePermissions(IEnumerable<string> granted, IReadOnlySet<string> enabledModules)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(enabledModules);

        return granted
            .Where(ContainsPermission)
            .Where(p => IsEnabled(AccessKeys.ModuleOf(p), enabledModules))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Tenant için açık modüllerin hazır rolleri (ADR-0005), modül sırasıyla; sonda "Salt okur": açık modüllerin tüm
    /// görüntüleme izinleri, yönetim izni olmadan. Hiçbir modül açık değilse "Salt okur" da yoktur.
    /// </summary>
    public IReadOnlyList<RoleTemplate> TemplatesFor(IReadOnlySet<string> enabledModules)
    {
        ArgumentNullException.ThrowIfNull(enabledModules);
        var open = Modules.Where(m => !IsAlwaysEnabled(m.Key) && IsEnabled(m.Key, enabledModules)).ToList();
        var templates = open.SelectMany(m => m.RoleTemplates).ToList();

        var reads = open.SelectMany(m => m.Permissions).Select(p => p.Key)
            .Where(k => k.EndsWith(".read", StringComparison.Ordinal))
            .ToList();
        if (reads.Count > 0)
        {
            templates.Add(new RoleTemplate(
                ReadOnlyTemplateKey, "Salt okur", "Açık modüllerin tüm kayıtlarını görüntüler; düzenleyemez ve yönetim yapamaz.", reads));
        }

        return templates;
    }

    /// <summary>Modül katalogda var ve tenant için açık mı?</summary>
    public bool IsEnabled(string moduleKey, IReadOnlySet<string> enabledModules) =>
        ContainsModule(moduleKey) && (IsAlwaysEnabled(moduleKey) || enabledModules.Contains(moduleKey));

    // Anahtar modülle başlar; olay türleri bilinir ve abone en güncel sürümü okuyabilir; izinler modülün kendi izinleridir;
    // abone sınıfı IEventSubscriber uygular. Kayıt türleri kurucunun sonunda, bütün modüller okununca denetlenir.
    private static void ValidateSubscription(IModule module, EventSubscription subscription)
    {
        var key = subscription.Key;
        if (!key.StartsWith(module.Key + ".", StringComparison.Ordinal) || key.Length > SubscriptionKeyMaxLength || !SubscriptionKeyPattern().IsMatch(key))
        {
            throw new InvalidOperationException(
                $"Geçersiz olay aboneliği anahtarı: '{key}' ({module.Key}). Biçim: '{module.Key}.ad' (küçük harf, rakam, tire).");
        }

        if (subscription.EventTypes.Count == 0)
        {
            throw new InvalidOperationException($"'{key}' aboneliğinde olay türü yok.");
        }

        foreach (var eventType in subscription.EventTypes)
        {
            if (!DomainEventTypes.SchemaVersions.TryGetValue(eventType, out var version))
            {
                throw new InvalidOperationException($"'{key}' aboneliğinde bilinmeyen olay türü: '{eventType}'.");
            }

            if (subscription.MaxSchemaVersion < version)
            {
                throw new InvalidOperationException(
                    $"'{key}' aboneliği '{eventType}' olayının {version}. sürümünü okuyamıyor (en fazla {subscription.MaxSchemaVersion}).");
            }
        }

        var foreign = subscription.Permissions.Where(p => module.Permissions.All(d => d.Key != p)).ToList();
        if (foreign.Count > 0)
        {
            throw new InvalidOperationException($"'{key}' aboneliğinde modülün tanımlamadığı izin var: {string.Join(", ", foreign)}.");
        }

        if (!typeof(IEventSubscriber).IsAssignableFrom(subscription.Subscriber) || subscription.Subscriber.IsAbstract)
        {
            throw new InvalidOperationException($"'{key}' aboneliğinin abonesi {subscription.Subscriber.Name} IEventSubscriber uygulamıyor.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+(-[a-z0-9]+)*)+$")]
    private static partial Regex SubscriptionKeyPattern();

    // Ayar anahtarı modülle başlar ve kayıtlı anahtar biçimindedir; varsayılan değer tanıma uyar.
    private static void ValidateSetting(string moduleKey, SettingDefinition setting)
    {
        string normalized;
        try
        {
            normalized = TenantSetting.NormalizeKey(setting.Key);
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
        }

        if (normalized != setting.Key || !setting.Key.StartsWith(moduleKey + ".", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Geçersiz ayar anahtarı: '{setting.Key}' ({moduleKey}). Biçim: '{moduleKey}.grup.ad'.");
        }

        if (setting.Type == SettingType.Choice && setting.Options.Count == 0)
        {
            throw new InvalidOperationException($"'{setting.Key}' seçenekli ama seçeneği yok.");
        }

        if (SettingRules.Validate(setting, setting.DefaultValue) is { } error)
        {
            throw new InvalidOperationException($"'{setting.Key}' varsayılan değeri geçersiz: {error}");
        }
    }

    // Şablon anahtarı modülle başlar; izinleri modülün kendi izinleridir (modüller birbirinin iznini bilmez).
    private static void ValidateTemplates(IModule module)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in module.RoleTemplates)
        {
            if (!template.Key.StartsWith(module.Key + ".", StringComparison.Ordinal) || !keys.Add(template.Key))
            {
                throw new InvalidOperationException(
                    $"Geçersiz veya tekrar eden rol şablonu anahtarı: '{template.Key}' ({module.Key}). Biçim: '{module.Key}.ad'.");
            }

            var foreign = template.Permissions.Where(p => module.Permissions.All(d => d.Key != p)).ToList();
            if (foreign.Count > 0)
            {
                throw new InvalidOperationException(
                    $"'{template.Key}' şablonunda modülün tanımlamadığı izin var: {string.Join(", ", foreign)}.");
            }
        }
    }
}
