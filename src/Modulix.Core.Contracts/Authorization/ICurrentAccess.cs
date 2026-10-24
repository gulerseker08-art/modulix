namespace Modulix.Core.Contracts.Authorization;

/// <summary>
/// İsteğin aktif tenant'ında oturumdaki kullanıcının erişimi. Aktif tenant yoksa her sorgu
/// <c>false</c> döner. Sonuç istek boyunca saklanır; rol veya entitlement değişikliği bir sonraki
/// istekte geçerli olur.
/// </summary>
public interface ICurrentAccess
{
    /// <summary>Modül aktif tenant için açık mı? Core modülü her zaman açıktır.</summary>
    Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken cancellationToken);

    /// <summary>
    /// Kullanıcının rollerinden biri bu izni veriyor ve iznin modülü tenant için açık mı?
    /// </summary>
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken);

    /// <summary>Kullanıcının aktif tenant'taki geçerli izinleri.</summary>
    Task<IReadOnlySet<string>> GetPermissionsAsync(CancellationToken cancellationToken);

    /// <summary>Kullanıcının kayıt kapsamı (Y8): rollerinin en genişi. Aktif tenant yoksa yalnızca kendi kayıtları.</summary>
    Task<RecordScope> GetScopeAsync(CancellationToken cancellationToken);
}
