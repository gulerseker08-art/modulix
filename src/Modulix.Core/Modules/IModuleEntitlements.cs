namespace Modulix.Core.Modules;

/// <summary>Tenant'ların modül hakları. Değişiklik ilgili tenant'ın bir sonraki isteğinde geçerli olur.</summary>
public interface IModuleEntitlements
{
    /// <summary>Tenant için açık modüllerin anahtarları (Core hariç; Core her zaman açıktır).</summary>
    Task<IReadOnlySet<string>> GetEnabledAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Modülü tenant için açar veya kapatır. Katalogda olmayan modül veya Core için
    /// <see cref="ArgumentException"/> fırlatır. <paramref name="terms"/> yalnızca açarken geçerlidir (kaynak, bitiş, not);
    /// verilmezse mevcut koşullar korunur.
    /// </summary>
    Task SetEnabledAsync(Guid tenantId, string moduleKey, bool enabled, CancellationToken cancellationToken, EntitlementTerms? terms = null);
}
