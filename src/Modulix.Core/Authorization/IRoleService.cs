namespace Modulix.Core.Authorization;

public sealed record RoleSummary(Guid Id, string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Aktif tenant'ın rolleri ve üyelik rol atamaları. Aktif tenant yoksa yazma işlemleri
/// <see cref="InvalidOperationException"/> fırlatır. Geçersiz ad, katalogda olmayan izin veya
/// tenant'ta bulunmayan rol için <see cref="ArgumentException"/> fırlatır.
/// </summary>
public interface IRoleService
{
    /// <summary>Ada göre sıralı.</summary>
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken);

    Task<RoleSummary> CreateAsync(string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);

    /// <summary>Rol bu tenant'ta yoksa <c>null</c>.</summary>
    Task<RoleSummary?> UpdateAsync(Guid roleId, string name, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);

    /// <summary>
    /// Üyeliğin rollerini verilen listeyle değiştirir. Üyelik aktif tenant'a ait değilse <c>false</c>.
    /// </summary>
    Task<bool> SetMembershipRolesAsync(Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);

    /// <summary>Üyeliğin rolleri; üyelik aktif tenant'a ait değilse <c>null</c>.</summary>
    Task<IReadOnlyList<RoleSummary>?> GetMembershipRolesAsync(Guid membershipId, CancellationToken cancellationToken);
}
