namespace Modulix.Core.Teams;

/// <summary>Ekip listesi satırı.</summary>
public sealed record TeamSummary(Guid Id, string Name, string? Description, int MemberCount);

/// <summary>Ekibin bir üyesi (üyelik kimliğiyle).</summary>
public sealed record TeamMemberSummary(Guid MembershipId, Guid UserId, string DisplayName, string Email);

public sealed record TeamDetail(Guid Id, string Name, string? Description, IReadOnlyList<TeamMemberSummary> Members);

/// <summary>
/// Aktif tenant'ın ekipleri (Y8). Çağıran <c>core.users.manage</c> iznine sahip olmalıdır (API denetler).
/// Geçersiz ad veya bu tenant'ta olmayan üyelik için <see cref="ArgumentException"/> (ParamName alan adıdır).
/// Değişiklikler <c>core.team</c> kaydı olarak denetim kaydına yazılır. Ekip silinince üyelikleri de silinir;
/// kayıtlar kişiye bağlı olduğu için onlara dokunulmaz.
/// </summary>
public interface ITeamService
{
    /// <summary>Ada göre sıralı, üye sayılarıyla.</summary>
    Task<IReadOnlyList<TeamSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Ekip bu tenant'ta yoksa null.</summary>
    Task<TeamDetail?> GetAsync(Guid teamId, CancellationToken cancellationToken);

    Task<TeamDetail> CreateAsync(string name, string? description, CancellationToken cancellationToken);

    /// <summary>Ekip yoksa null.</summary>
    Task<TeamDetail?> UpdateAsync(Guid teamId, string name, string? description, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>Ekibin üyelerini verilen listeyle değiştirir. Ekip yoksa null.</summary>
    Task<TeamDetail?> SetMembersAsync(Guid teamId, IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken);

    /// <summary>Kişinin ekipleri (ada göre). Üyelik bu tenant'ta yoksa null.</summary>
    Task<IReadOnlyList<TeamSummary>?> GetMembershipTeamsAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>Kişinin ekiplerini verilen listeyle değiştirir. Üyelik bu tenant'ta yoksa false.</summary>
    Task<bool> SetMembershipTeamsAsync(Guid membershipId, IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken);
}
