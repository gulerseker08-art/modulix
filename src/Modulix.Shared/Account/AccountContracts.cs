namespace Modulix.Shared.Account;

// Kişinin kendi hesabı (Y7a): /api/account. Oturum ister, firma ve izin istemez.

public sealed record AccountProfileResponse(string DisplayName, string Email);

public sealed record UpdateProfileRequest(string DisplayName);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Yeni adrese onay bağlantısı gider; e-posta bağlantı açılınca değişir.</summary>
public sealed record ChangeEmailRequest(string Email, string CurrentPassword);

/// <summary>Açık oturum. <see cref="Device"/>: tarayıcı ve işletim sistemi (ör. "Chrome · Windows").</summary>
public sealed record SessionDto(Guid Id, string Device, string? IpAddress, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool IsCurrent);

public sealed record CloseSessionsResponse(int Closed);

// Oturum gerektirmeyenler: /api/auth.

public sealed record ForgotPasswordRequest(string Email);

public sealed record ConfirmEmailChangeRequest(Guid UserId, string Email, string Token);
