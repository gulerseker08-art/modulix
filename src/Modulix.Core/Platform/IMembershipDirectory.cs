namespace Modulix.Core.Platform;

/// <summary>Bir kullanıcının etkin üyeliği; tenant da etkin olmalıdır.</summary>
public sealed record MembershipSummary(Guid MembershipId, Guid TenantId, string TenantName, string TenantSlug);

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
}
