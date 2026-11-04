namespace Modulix.Core.Modules;

/// <summary>
/// Açık bir modül hakkının süre evresi (ADR-0028). Evre saklanmaz; bitiş tarihinden ve ek süreden her okumada hesaplanır,
/// bu yüzden bir işçinin çalışmasına bağlı değildir. Kapalı hak evresizdir.
/// </summary>
public enum EntitlementPhase
{
    /// <summary>Bitiş yok ya da bitiş tarihi gelmedi. Okuma ve yazma açık.</summary>
    Active = 1,

    /// <summary>Bitiş geçti, ek süre sürüyor. Okuma ve yazma açık; yöneticiye uyarı gösterilir.</summary>
    Grace = 2,

    /// <summary>Ek süre de bitti. Yalnızca okuma; yazma reddedilir, veri silinmez.</summary>
    ReadOnly = 3,
}

/// <summary>Bir modül hakkının süre durumu. <see cref="GraceEndsAt"/> yalnızca bitiş tarihi olan haklarda dolar.</summary>
public sealed record ModuleLicenseInfo(string ModuleKey, EntitlementPhase Phase, DateTimeOffset? ValidUntil, DateTimeOffset? GraceEndsAt);

public static class EntitlementPhases
{
    /// <summary>Varsayılan ek süre (gün): bitişten sonra yazma bu kadar daha açık kalır.</summary>
    public const int DefaultGraceDays = 14;

    public const int MaxGraceDays = 365;

    /// <summary>Bitiş tarihinin kendisi (ve ek sürenin son anı) hâlâ önceki evrededir.</summary>
    public static EntitlementPhase At(DateTimeOffset? validUntil, DateTimeOffset now, int graceDays)
    {
        if (validUntil is not { } until || now <= until)
        {
            return EntitlementPhase.Active;
        }

        return now <= until.AddDays(graceDays) ? EntitlementPhase.Grace : EntitlementPhase.ReadOnly;
    }

    public static ModuleLicenseInfo Describe(string moduleKey, DateTimeOffset? validUntil, DateTimeOffset now, int graceDays) =>
        new(moduleKey, At(validUntil, now, graceDays), validUntil, validUntil?.AddDays(graceDays));
}

/// <summary>
/// Modül haklarının süre evresi (ADR-0028). Bir modül açıksa (<see cref="IModuleEntitlements.GetEnabledAsync"/>) okunabilir;
/// yazma yalnızca <see cref="EntitlementPhase.ReadOnly"/> dışındaki evrelerde açıktır. Çekirdek her zaman yazılabilir.
/// </summary>
public interface IModuleLicense
{
    /// <summary>Tenant için açık modüllerin süre durumu; çekirdek ve kapalı modüller yoktur.</summary>
    Task<IReadOnlyDictionary<string, ModuleLicenseInfo>> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Modül açıksa ve salt okunur değilse <c>true</c>.</summary>
    Task<bool> IsWritableAsync(Guid tenantId, string moduleKey, CancellationToken cancellationToken);
}

public static class ModuleLicenseExtensions
{
    /// <summary>
    /// Oturumsuz (genel) uç noktalar için: okuma modül açıksa, yazma ayrıca salt okunur değilse serbesttir. Çekirdek her zaman serbesttir.
    /// </summary>
    public static async Task<bool> AllowsAsync(
        this IModuleLicense license, Guid tenantId, string moduleKey, bool write, CancellationToken cancellationToken) =>
        ModuleCatalog.IsAlwaysEnabled(moduleKey)
        || (write
            ? await license.IsWritableAsync(tenantId, moduleKey, cancellationToken)
            : (await license.GetAsync(tenantId, cancellationToken)).ContainsKey(moduleKey));
}
