namespace Modulix.Core.Organization;

/// <summary>
/// Departman listesi satırı. <see cref="PathNames"/> kökten kendine adlardır (ör. "Merkez › Marmara Bayi").
/// </summary>
public sealed record BusinessUnitSummary(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Code,
    string Kind,
    int Depth,
    bool IsActive,
    int MemberCount,
    string PathNames);

/// <summary>Departmanın bir üyesi (üyelik kimliğiyle).</summary>
public sealed record BusinessUnitMemberSummary(Guid MembershipId, Guid UserId, string DisplayName, string Email);

public sealed record BusinessUnitDetail(BusinessUnitSummary Unit, IReadOnlyList<BusinessUnitMemberSummary> Members);

/// <summary>Kullanıcının departmanı.</summary>
public sealed record MembershipBusinessUnit(Guid Id, string Name, string PathNames, string Kind, bool IsActive);

/// <summary>
/// İstek kurallara uygun ama ağacın durumuyla çelişiyor: aynı üst departmanda aynı ad, üyesi veya açık alt departmanı olan
/// departmanı kapatmak gibi. API 409 döner.
/// </summary>
public sealed class BusinessUnitConflictException(string message) : Exception(message);

/// <summary>
/// Aktif tenant'ın departman ağacı (ADR-0026). Çağıran <c>core.users.manage</c> iznine sahip olmalıdır (API denetler).
/// Geçersiz alan için <see cref="ArgumentException"/> (ParamName alan adıdır), ağaç kuralı çatışması için
/// <see cref="BusinessUnitConflictException"/>. Değişiklikler <c>core.businessunit</c> (departman) ve <c>core.membership</c>
/// (kullanıcının departmanı) kaydı olarak denetim kaydına yazılır.
///
/// Kurallar: tenant'ın tek kök departmanı vardır ve kapatılamaz; üst departman etkin olmalıdır; ağaç en fazla dört seviyedir;
/// aynı üst departmanda aynı ad kullanılamaz; üyesi veya etkin alt departmanı olan departman kapatılamaz; kapalı departmana
/// kullanıcı atanamaz. Departman silinmez.
/// </summary>
public interface IBusinessUnitService
{
    /// <summary>Ağacın tamamı (kapalılar dahil), seviye ve ada göre sıralı.</summary>
    Task<IReadOnlyList<BusinessUnitSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Departman bu tenant'ta yoksa null.</summary>
    Task<BusinessUnitDetail?> GetAsync(Guid businessUnitId, CancellationToken cancellationToken);

    /// <summary>Üst departman bu tenant'ta yoksa null.</summary>
    Task<BusinessUnitDetail?> CreateAsync(Guid parentId, string name, string? code, string kind, CancellationToken cancellationToken);

    /// <summary>Ad, kod ve tür değişir; ağaçtaki yeri değişmez. Departman yoksa null.</summary>
    Task<BusinessUnitDetail?> UpdateAsync(Guid businessUnitId, string name, string? code, string kind, CancellationToken cancellationToken);

    /// <summary>Departmanı kapatır ya da yeniden açar. Departman yoksa null.</summary>
    Task<BusinessUnitDetail?> SetActiveAsync(Guid businessUnitId, bool isActive, CancellationToken cancellationToken);

    /// <summary>Kullanıcının departmanı. Üyelik bu tenant'ta yoksa null.</summary>
    Task<MembershipBusinessUnit?> GetMembershipBusinessUnitAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>
    /// Kullanıcıyı departmana taşır. Bu, kullanıcının sahip olduğu kayıtların görünürlüğünü değiştirir (ADR-0026 K7); bu dilimde
    /// kapsam henüz departmana bağlı değildir. Üyelik bu tenant'ta yoksa false.
    /// </summary>
    Task<bool> SetMembershipBusinessUnitAsync(Guid membershipId, Guid businessUnitId, CancellationToken cancellationToken);
}
