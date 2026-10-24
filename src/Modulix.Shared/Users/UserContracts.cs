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
    bool IsCurrentUser)
{
    public bool IsAdministrator => Roles.Any(r => r.IsSystem);
}

public sealed record UserPageResponse(IReadOnlyList<UserDto> Items, int Page, int PageSize, int TotalCount);

public sealed record InviteUserRequest(string Email, string DisplayName, IReadOnlyList<Guid>? RoleIds);

/// <summary><see cref="ExistingAccount"/>: kişinin hesabı vardı; üyelik hemen etkin oldu ve bilgi e-postası gitti.</summary>
public sealed record InviteUserResponse(UserDto User, bool ExistingAccount);

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
