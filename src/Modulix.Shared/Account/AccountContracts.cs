namespace Modulix.Shared.Account;

// Kişinin kendi hesabı (Y7a): /api/account. Oturum ister, firma ve izin istemez.

/// <summary><see cref="PreferredLanguage"/>: kişinin seçtiği arayüz dili (ADR-0034); boşsa firmanın varsayılan dili geçerlidir.</summary>
public sealed record AccountProfileResponse(string DisplayName, string Email, string? PreferredLanguage = null);

public sealed record UpdateProfileRequest(string DisplayName);

/// <summary><see cref="Language"/>: <c>tr</c>, <c>en</c>, <c>az</c>; boş gönderilirse tercih kaldırılır ve firmanın varsayılanı geçerli olur.</summary>
public sealed record UpdateLanguageRequest(string? Language);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Yeni adrese onay bağlantısı gider; e-posta bağlantı açılınca değişir.</summary>
public sealed record ChangeEmailRequest(string Email, string CurrentPassword);

/// <summary>Açık oturum. <see cref="Device"/>: tarayıcı ve işletim sistemi (ör. "Chrome · Windows").</summary>
public sealed record SessionDto(Guid Id, string Device, string? IpAddress, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool IsCurrent);

public sealed record CloseSessionsResponse(int Closed);

// Oturum gerektirmeyenler: /api/auth.

public sealed record ForgotPasswordRequest(string Email);

public sealed record ConfirmEmailChangeRequest(Guid UserId, string Email, string Token);

// İki adımlı doğrulama (Y7b): /api/account/two-factor.

/// <summary><see cref="RequiredBy"/>: zorunlu kılan firmalar; açıkken kapatılamaz, kapalıyken o firmalara girilemez.</summary>
public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft, IReadOnlyList<string> RequiredBy);

public sealed record StartTwoFactorRequest(string CurrentPassword);

/// <summary>Doğrulama uygulamasına girilecek anahtar ve aynı bilginin QR kodu (PNG, data: adresi).</summary>
public sealed record TwoFactorSetupResponse(string SharedKey, string QrCode);

public sealed record EnableTwoFactorRequest(string Code);

public sealed record DisableTwoFactorRequest(string CurrentPassword, string Code);

public sealed record RegenerateRecoveryCodesRequest(string CurrentPassword);

/// <summary>Kurtarma kodları yalnızca bir kez gösterilir.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);
