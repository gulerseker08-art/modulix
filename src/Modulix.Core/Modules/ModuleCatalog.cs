using Modulix.Core.Contracts.Modules;

namespace Modulix.Core.Modules;

/// <summary>
/// Uygulamada kayıtlı modüller ve izinleri. Açılışta bir kez oluşturulur; anahtar biçimi,
/// tekrar eden modül veya izin, modül anahtarıyla başlamayan izin varsa uygulama açılmaz.
/// </summary>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, IModule> modules;
    private readonly Dictionary<string, PermissionDefinition> permissions;

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
        }

        if (!this.modules.ContainsKey(CoreModule.ModuleKey))
        {
            throw new InvalidOperationException($"Katalogda '{CoreModule.ModuleKey}' modülü yok.");
        }

        Modules = [.. this.modules.Values.OrderBy(m => m.Key, StringComparer.Ordinal)];
    }

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

    public bool ContainsPermission(string permissionKey) => permissions.ContainsKey(permissionKey);

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

    /// <summary>Modül katalogda var ve tenant için açık mı?</summary>
    public bool IsEnabled(string moduleKey, IReadOnlySet<string> enabledModules) =>
        ContainsModule(moduleKey) && (IsAlwaysEnabled(moduleKey) || enabledModules.Contains(moduleKey));
}
