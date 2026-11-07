namespace Modulix.Core.Platform;

/// <summary>Bir kullanıcının etkin üyeliği; tenant da etkin olmalıdır.</summary>
/// <summary><see cref="IsSupport"/>: üyelik bir destek erişimidir (ADR-0028); istekleri okuma günlüğüne yazılır.</summary>
public sealed record MembershipSummary(Guid MembershipId, Guid TenantId, string TenantName, string TenantSlug, bool IsSupport = false);

/// <summary>Aktif tenant'ın bir işletici şirketi (ADR-0008).</summary>
public sealed record CompanySummary(Guid Id, string Name, bool IsDefault);

/// <summary>
/// Üyeliğin erişebildiği işletici şirketler (ADR-0008, adım 2). Yönetici (sistem rolü) tenant'ın bütün şirketlerine
/// erişir (<see cref="IsAllCompanies"/>); yönetici olmayan yalnızca görevlendirildiği şirketleri görür. Şirketler ada
/// göre sıralıdır.
/// </summary>
public sealed record CompanyAccess(IReadOnlyList<CompanySummary> Companies, bool IsAllCompanies);

/// <summary>
/// Üyelik sorguları. Aktif tenant her istekte bu servisle yeniden doğrulanır; üyelik
/// devre dışı bırakılırsa veya tenant kapatılırsa erişim bir sonraki istekte kalkar.
/// </summary>
public interface IMembershipDirectory
{
    /// <summary>Kullanıcının etkin üyelikleri (yalnızca etkin tenant'lar), tenant adına göre sıralı.</summary>
    Task<IReadOnlyList<MembershipSummary>> GetActiveForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Etkin üyelik yoksa veya tenant kapalıysa <c>null</c>.</summary>
    Task<MembershipSummary?> FindActiveAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Üyeliğin son giriş zamanını şimdi yapar (giriş ve tenant seçiminde).</summary>
    Task RecordSignInAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliğin erişebildiği işletici şirketler (ADR-0008, adım 2). Aktif tenant'ın oturum bağlamında çağrılır;
    /// Row-Level Security yalnızca aktif tenant'ın şirketlerini döndürür.
    /// </summary>
    Task<CompanyAccess> GetAccessibleCompaniesAsync(Guid membershipId, CancellationToken cancellationToken);
}
