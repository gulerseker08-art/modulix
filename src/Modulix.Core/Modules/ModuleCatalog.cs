using Modulix.Core.Contracts.Modules;
using Modulix.Core.Settings;

namespace Modulix.Core.Modules;

/// <summary>
/// Uygulamada kayıtlı modüller ve izinleri. Açılışta bir kez oluşturulur; anahtar biçimi,
/// tekrar eden modül veya izin, modül anahtarıyla başlamayan izin varsa uygulama açılmaz.
/// </summary>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, IModule> modules;
    private readonly Dictionary<string, PermissionDefinition> permissions;
    private readonly Dictionary<string, AuditedEntity> auditedEntities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ModuleKey, SettingDefinition Definition)> settings = new(StringComparer.Ordinal);

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

        Modules = [.. this.modules.Values.OrderBy(m => m.Key, StringComparer.Ordinal)];
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
