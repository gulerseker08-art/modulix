using Modulix.Shared.Access;

namespace Modulix.Shared.Users;

// Kullanıcı yönetimi (/api/users) ile davet ve şifre sıfırlama bağlantılarının (/api/auth) sözleşmeleri.

public enum UserStatus
{
    Active = 1,
    Disabled = 2,

    /// <summary>Davet gönderildi, kişi henüz şifresini belirlemedi.</summary>
    Invited = 3,
}

public sealed record UserRoleDto(Guid Id, string Name, bool IsSystem);

/// <summary>Kuruluşun bir üyesi. Kimlik <see cref="MembershipId"/>'dir; işlemler bu kimlikle yapılır.</summary>
public sealed record UserDto(
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    string Email,
    UserStatus Status,
    IReadOnlyList<UserRoleDto> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? InvitedAt,
    DateTimeOffset? LastSignInAt,
    bool IsLockedOut,
    bool IsCurrentUser,
    bool TwoFactorEnabled = false)
{
    public bool IsAdministrator => Roles.Any(r => r.IsSystem);
}

public sealed record UserPageResponse(IReadOnlyList<UserDto> Items, int Page, int PageSize, int TotalCount);

public sealed record InviteUserRequest(string Email, string DisplayName, IReadOnlyList<Guid>? RoleIds);

/// <summary><see cref="ExistingAccount"/>: kişinin hesabı vardı; üyelik hemen etkin oldu ve bilgi e-postası gitti.</summary>
public sealed record InviteUserResponse(UserDto User, bool ExistingAccount);

/// <summary>Kişinin sorumlu olduğu bir kayıt türü; <see cref="Count"/> boşsa işlemi yapan bu kayıtları göremiyor.</summary>
public sealed record OwnedRecordsDto(string EntityKey, string Label, int? Count);

/// <summary>Kişinin bir geçerli izni ve onu veren roller.</summary>
public sealed record PermissionGrantDto(string Key, IReadOnlyList<UserRoleDto> GrantedBy);

/// <summary>
/// "Bu kullanıcı neye erişebilir": <see cref="Permissions"/> kişinin geçerli izinleri, <see cref="Assignable"/> tenant için
/// açık modüllerin tüm izinleri (ekran matrisi bunlarla çizer).
/// </summary>
public sealed record UserAccessDto(bool IsAdministrator, IReadOnlyList<PermissionDto> Assignable, IReadOnlyList<PermissionGrantDto> Permissions);

/// <summary><see cref="TransferToMembershipId"/> verilirse kişinin kayıtları önce bu üyeye devredilir.</summary>
public sealed record DisableUserRequest(Guid? TransferToMembershipId);

public sealed record TransferRecordsRequest(Guid ToMembershipId);

public sealed record TransferRecordsResponse(int Transferred);

public sealed record InvitationInfoResponse(string TenantName, string Email, string DisplayName);

public sealed record AcceptInvitationRequest(Guid MembershipId, string Token, string Password);

public sealed record ResetPasswordRequest(Guid UserId, string Token, string Password);

public static class UserLabels
{
    public static string Of(UserStatus status) => status switch
    {
        UserStatus.Active => "Etkin",
        UserStatus.Disabled => "Devre dışı",
        UserStatus.Invited => "Davet bekliyor",
        _ => status.ToString(),
    };
}
