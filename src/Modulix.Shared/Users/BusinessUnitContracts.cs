namespace Modulix.Shared.Users;

// Departman ağacı (/api/business-units, ADR-0026). Departman, kullanıcının hangi kayıtları göreceğini ve hangi rolleri
// devraldığını belirleyen örgüt yapısıdır; merkez kök, bayiler altındadır.

public static class BusinessUnitKindLabels
{
    public const string Center = "Center";
    public const string Dealer = "Dealer";
    public const string Other = "Other";

    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>
    {
        [Center] = "Merkez",
        [Dealer] = "Bayi",
        [Other] = "Diğer",
    };

    public static string Of(string kind) => Labels.TryGetValue(kind, out var label) ? label : kind;
}

/// <summary>Departman. <see cref="PathNames"/> kökten kendine adlardır (ör. "Merkez › Marmara Bayi").</summary>
public sealed record BusinessUnitDto(
    Guid Id,
    Guid? ParentId,
    string Name,
    string? Code,
    string Kind,
    int Depth,
    bool IsActive,
    int MemberCount,
    string PathNames);

public sealed record BusinessUnitMemberDto(Guid MembershipId, Guid UserId, string DisplayName, string Email);

public sealed record BusinessUnitDetailDto(BusinessUnitDto Unit, IReadOnlyList<BusinessUnitMemberDto> Members);

public sealed record CreateBusinessUnitRequest(Guid ParentId, string Name, string? Code, string Kind);

public sealed record UpdateBusinessUnitRequest(string Name, string? Code, string Kind);

public sealed record SetBusinessUnitActiveRequest(bool IsActive);

/// <summary>Departmana atanmış rol (ADR-0026 K6); <see cref="Scope"/> rolün kayıt kapsamıdır.</summary>
public sealed record BusinessUnitRoleDto(Guid RoleId, string RoleName, Access.RecordScope Scope, bool InheritToChildren);

public sealed record BusinessUnitRoleItem(Guid RoleId, bool InheritToChildren = false);

/// <summary>Departmanın rollerini verilen listeyle değiştirir.</summary>
public sealed record SetBusinessUnitRolesRequest(IReadOnlyList<BusinessUnitRoleItem> Roles);

/// <summary>Kullanıcının departmanı (kullanıcı kartı).</summary>
public sealed record MembershipBusinessUnitDto(Guid Id, string Name, string PathNames, string Kind, bool IsActive);

/// <summary>
/// Kullanıcının departmanını değiştirir (ADR-0026 K7). Kullanıcının kayıtları varsa <see cref="MoveRecordsWithUser"/> (kayıtlar
/// kullanıcıyla yeni departmana geçer) ya da <see cref="TransferRecordsToMembershipId"/> (kayıtlar eski departmanda kalan etkin bir
/// üyeye devredilir) verilmelidir; ikisi birden verilmez. Kayıt varsa ya da kullanıcı etkin değilse <see cref="Reason"/> zorunludur.
/// </summary>
public sealed record SetMembershipBusinessUnitRequest(
    Guid BusinessUnitId,
    string? Reason = null,
    bool MoveRecordsWithUser = false,
    Guid? TransferRecordsToMembershipId = null);

/// <summary>Departman değişikliğinin etkisi; hiçbir şeyi değiştirmeyen önizleme (<c>GET .../business-unit/change-preview</c>).</summary>
public sealed record BusinessUnitChangePreviewDto(
    Guid MembershipId,
    string DisplayName,
    UserStatus Status,
    Guid? CurrentBusinessUnitId,
    string? CurrentPath,
    Guid TargetBusinessUnitId,
    string TargetPath,
    bool IsChange,
    IReadOnlyList<OwnedRecordsDto> OwnedRecords,
    int OwnedTotal,
    bool OwnedUnknown,
    bool DecisionRequired,
    bool ReasonRequired,
    IReadOnlyList<string> LostRoles,
    IReadOnlyList<string> GainedRoles,
    IReadOnlyList<string> Warnings);
