namespace Modulix.Core.Contracts.Tenancy;

/// <summary>Aktif tenant'ın etkin bir üyesi. Kayıtlarda sorumlu kişi olarak <see cref="UserId"/> saklanır.</summary>
public sealed record TenantMember(Guid UserId, string DisplayName);

/// <summary>
/// Aktif tenant'ın üyeleri. Modüller bir kaydı kişiye atarken (sorumlu, atanan) bu servisle doğrular;
/// başka tenant'ın kullanıcısı veya üyeliği kapatılmış biri üye sayılmaz. Aktif tenant yoksa liste boştur.
/// </summary>
public interface ITenantMembers
{
    Task<IReadOnlyList<TenantMember>> ListActiveAsync(CancellationToken cancellationToken);

    Task<bool> IsActiveMemberAsync(Guid userId, CancellationToken cancellationToken);
}
