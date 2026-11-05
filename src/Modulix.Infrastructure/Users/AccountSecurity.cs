using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Users;

public sealed class AccountOptions
{
    public const string SectionName = "Account";

    /// <summary>"Şifremi unuttum": bir IP adresinden bir saatte kabul edilen istek sayısı.</summary>
    public int ForgotPasswordPerIpPerHour { get; set; } = 10;

    /// <summary>"Şifremi unuttum": aynı hesaba iki bağlantı arasında beklenecek süre (arada gelen istekler sessizce yok sayılır).</summary>
    public TimeSpan ForgotPasswordInterval { get; set; } = TimeSpan.FromMinutes(15);
}

public enum ForgotPasswordResult
{
    Accepted = 1,
    TooManyRequests = 2,
}

public enum EmailChangeResult
{
    Changed = 1,

    /// <summary>Bağlantı geçersiz, kullanılmış veya süresi dolmuş.</summary>
    InvalidLink = 2,

    /// <summary>Adres başka bir hesapta kullanılıyor; hangisi olduğu söylenmez.</summary>
    AddressUnavailable = 3,
}

/// <summary>
/// Kişinin kendi hesabı (Y7a): ad, şifre, e-posta ve "Şifremi unuttum". Bilgiler kişiye aittir, firmaya değil; değişiklik
/// kişinin üye olduğu her firmada görünür ve her firmanın denetim kaydına "Hesap" olarak yazılır. Alan hataları
/// <see cref="ArgumentException"/> olarak döner (ParamName alan adıdır).
/// </summary>
public sealed class AccountSecurity(
    ModulixDbContext db,
    UserManager<ModulixUser> users,
    UserSessions sessions,
    AccountAudit audit,
    IEmailQueue emails,
    IMemoryCache cache,
    IOptions<AccountOptions> options)
{
    public async Task UpdateDisplayNameAsync(ModulixUser user, string? displayName, AccountClient client, CancellationToken cancellationToken)
    {
        var name = UserAdministration.ValidateDisplayName(displayName);
        if (name == user.DisplayName)
        {
            return;
        }

        var before = user.DisplayName;
        user.DisplayName = name;
        ThrowIfFailed(await users.UpdateAsync(user));
        await audit.RecordAsync(user, AuditAction.Updated, null, client, cancellationToken,
            before: new JsonObject { ["displayName"] = before }, details: new JsonObject { ["displayName"] = name });
    }

    /// <summary>
    /// Arayüz dili tercihini kaydeder (ADR-0033). Boş değer tercihi kaldırır; desteklenmeyen kod reddedilir.
    /// Dil kişiye aittir: değişiklik üye olunan bütün firmalarda geçerlidir.
    /// </summary>
    public async Task UpdateLanguageAsync(ModulixUser user, string? language, AccountClient client, CancellationToken cancellationToken)
    {
        string? preferred = null;
        if (!string.IsNullOrWhiteSpace(language))
        {
            preferred = LanguageOptions.Normalize(language)
                ?? throw new ArgumentException("Desteklenmeyen dil. Türkçe, English veya Azərbaycanca seçin.", "language");
        }

        var before = LanguageOptions.Normalize(user.PreferredLanguage);
        if (preferred == before)
        {
            return;
        }

        user.PreferredLanguage = preferred;
        ThrowIfFailed(await users.UpdateAsync(user));
        await audit.RecordAsync(user, AuditAction.Updated, null, client, cancellationToken,
            before: new JsonObject { ["language"] = before }, details: new JsonObject { ["language"] = preferred });
    }

    /// <summary>Şifreyi değiştirir ve <paramref name="currentSessionId"/> dışındaki oturumları kapatır.</summary>
    public async Task ChangePasswordAsync(
        ModulixUser user, string? currentPassword, string? newPassword, Guid? currentSessionId, AccountClient client, CancellationToken cancellationToken)
    {
        await EnsurePasswordAsync(user, currentPassword);

        var result = await users.ChangePasswordAsync(user, currentPassword!, newPassword ?? string.Empty);
        if (!result.Succeeded)
        {
            throw new ArgumentException(string.Join(" ", result.Errors.Select(e => e.Description)), "password");
        }

        var closed = await sessions.CloseAllAsync(user.Id, currentSessionId, cancellationToken);
        await audit.RecordAsync(user, AuditAction.PasswordChanged, null, client, cancellationToken,
            details: new JsonObject { ["sessions"] = closed });
    }

    /// <summary>Yeni adrese onay bağlantısı gönderir (24 saat); e-posta bağlantı açılınca değişir.</summary>
    public async Task RequestEmailChangeAsync(
        ModulixUser user, string? newEmail, string? currentPassword, string publicUrl, CancellationToken cancellationToken)
    {
        var email = UserAdministration.ValidateEmail(newEmail);
        if (string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bu zaten hesabınızın e-posta adresi.", "email");
        }

        await EnsurePasswordAsync(user, currentPassword);

        // Adres başka hesapta kullanılsa da bağlantı gider; böylece adreslerin kayıtlı olup olmadığı buradan yoklanamaz.
        var token = await users.GenerateChangeEmailTokenAsync(user, email);
        emails.Enqueue(UserEmails.ConfirmEmailChange(email, user.DisplayName, AccountTokens.EmailChangeLink(publicUrl, user.Id, email, token)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>E-postadaki onay bağlantısı: e-postayı (ve giriş adını) değiştirir, tüm oturumları kapatır, eski adrese bildirir.</summary>
    public async Task<EmailChangeResult> ConfirmEmailChangeAsync(
        Guid userId, string? email, string? token, string publicUrl, AccountClient client, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token)
            || string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase)
            || !await users.VerifyUserTokenAsync(
                user, users.Options.Tokens.ChangeEmailTokenProvider, UserManager<ModulixUser>.GetChangeEmailTokenPurpose(email), token))
        {
            return EmailChangeResult.InvalidLink;
        }

        if (await users.FindByEmailAsync(email) is { } other && other.Id != user.Id)
        {
            return EmailChangeResult.AddressUnavailable;
        }

        var before = user.Email!;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var changed = await users.ChangeEmailAsync(user, email, token);
            if (!changed.Succeeded)
            {
                return changed.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName))
                    ? EmailChangeResult.AddressUnavailable
                    : EmailChangeResult.InvalidLink;
            }

            var renamed = await users.SetUserNameAsync(user, email);
            if (!renamed.Succeeded)
            {
                return EmailChangeResult.AddressUnavailable;
            }

            await sessions.CloseAllAsync(user.Id, except: null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        emails.Enqueue(UserEmails.EmailChanged(before, user.DisplayName, email, AccountTokens.SignInLink(publicUrl)));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(user, AuditAction.Updated, null, client, cancellationToken, before: new JsonObject { ["email"] = before });
        return EmailChangeResult.Changed;
    }

    /// <summary>
    /// "Şifremi unuttum". Hesap olsun olmasın aynı sonuç döner. Aynı hesaba en fazla <see cref="AccountOptions.ForgotPasswordInterval"/>
    /// aralıkla bağlantı gider; şifresi olmayan (daveti bekleyen) hesaba gitmez, onun yolu davet bağlantısıdır.
    /// </summary>
    public async Task<ForgotPasswordResult> RequestPasswordResetAsync(
        string? email, string publicUrl, AccountClient client, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var ipKey = $"forgot-password:ip:{client.IpAddress}";
        var count = cache.GetOrCreate(ipKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new RequestCounter();
        })!;
        if (Interlocked.Increment(ref count.Value) > settings.ForgotPasswordPerIpPerHour)
        {
            return ForgotPasswordResult.TooManyRequests;
        }

        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            audit.UnknownAccount(client);
            return ForgotPasswordResult.Accepted;
        }

        var userKey = $"forgot-password:user:{user.Id}";
        if (!await users.HasPasswordAsync(user) || cache.TryGetValue(userKey, out _))
        {
            return ForgotPasswordResult.Accepted;
        }

        cache.Set(userKey, true, settings.ForgotPasswordInterval);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        emails.Enqueue(UserEmails.ForgotPassword(user.Email!, user.DisplayName, AccountTokens.PasswordResetLink(publicUrl, user.Id, token)));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(user, AuditAction.PasswordResetRequested, null, client, cancellationToken);
        return ForgotPasswordResult.Accepted;
    }

    private async Task EnsurePasswordAsync(ModulixUser user, string? currentPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            throw new ArgumentException("Mevcut şifrenizi girin.", "currentPassword");
        }

        if (!await users.CheckPasswordAsync(user, currentPassword))
        {
            // Açık oturumdan şifre denenmesin: hatalı denemeler girişteki gibi sayılır ve hesabı kilitler.
            await users.AccessFailedAsync(user);
            throw new ArgumentException("Mevcut şifre hatalı.", "currentPassword");
        }
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private sealed class RequestCounter
    {
        public int Value;
    }
}
