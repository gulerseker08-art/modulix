using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Platform;

namespace Modulix.Infrastructure.Users;

/// <summary>Kurulum: doğrulama uygulamasına girilecek anahtar ve QR koddaki adres (otpauth://).</summary>
public sealed record AuthenticatorSetup(string SharedKey, string Uri);

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

public enum TwoFactorCodeResult
{
    Invalid = 0,
    Authenticator = 1,
    RecoveryCode = 2,
}

/// <summary>
/// İki adımlı doğrulama (Y7b): doğrulama uygulaması (TOTP) ve tek kullanımlık kurtarma kodları. Hesaba aittir; firma
/// yalnızca zorunlu kılabilir (<see cref="CoreModule.SettingKeys.RequireTwoFactor"/>). Açma, kapatma ve kodları yenileme
/// kişinin her firmasının denetim kaydına "Hesap" olarak yazılır.
/// </summary>
public sealed class TwoFactorAccounts(
    ModulixDbContext db,
    UserManager<ModulixUser> users,
    UserSessions sessions,
    AccountAudit audit,
    IEmailQueue emails,
    ITenantScopeFactory tenantScopes)
{
    public const int RecoveryCodeCount = 10;

    private const string Issuer = "Modulix";

    public async Task<TwoFactorStatus> GetStatusAsync(ModulixUser user) =>
        new(user.TwoFactorEnabled, user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0);

    /// <summary>Yeni anahtar üretir; doğrulama <see cref="EnableAsync"/> ile koda bakılarak açılır.</summary>
    public async Task<AuthenticatorSetup> BeginSetupAsync(ModulixUser user, string? currentPassword)
    {
        if (user.TwoFactorEnabled)
        {
            throw Conflict("İki adımlı doğrulama zaten açık.");
        }

        await EnsurePasswordAsync(user, currentPassword);
        ThrowIfFailed(await users.ResetAuthenticatorKeyAsync(user));
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var label = Uri.EscapeDataString($"{Issuer}:{user.Email}");
        return new AuthenticatorSetup(
            FormatKey(key),
            $"otpauth://totp/{label}?secret={key}&issuer={Issuer}&digits=6");
    }

    /// <summary>Uygulamadaki kodu doğrulayıp açar; bir kez gösterilecek kurtarma kodlarını döner.</summary>
    public async Task<IReadOnlyList<string>> EnableAsync(ModulixUser user, string? code, AccountClient client, CancellationToken cancellationToken)
    {
        if (user.TwoFactorEnabled)
        {
            throw Conflict("İki adımlı doğrulama zaten açık.");
        }

        if (await users.GetAuthenticatorKeyAsync(user) is null)
        {
            throw Conflict("Önce kurulumu başlatın.");
        }

        if (!await VerifyAuthenticatorAsync(user, code))
        {
            throw new ArgumentException("Kod doğru değil. Uygulamadaki güncel 6 haneli kodu girin.", "code");
        }

        ThrowIfFailed(await users.SetTwoFactorEnabledAsync(user, true));
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        await audit.RecordAsync(user, AuditAction.TwoFactorEnabled, null, client, cancellationToken);
        return codes;
    }

    /// <summary>Şifre ve geçerli bir kod (uygulama ya da kurtarma) ister; kişinin bir firması zorunlu kılıyorsa kapatılamaz.</summary>
    public async Task DisableAsync(ModulixUser user, string? currentPassword, string? code, AccountClient client, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled)
        {
            return;
        }

        await EnsurePasswordAsync(user, currentPassword);
        if (await RequiringTenantsAsync(user.Id, cancellationToken) is { Count: > 0 } requiring)
        {
            throw Conflict($"{string.Join(", ", requiring)} iki adımlı doğrulamayı zorunlu kılıyor; kapatamazsınız.");
        }

        if (await VerifyCodeAsync(user, code) == TwoFactorCodeResult.Invalid)
        {
            throw new ArgumentException("Kod doğru değil. Uygulamadaki kodu veya bir kurtarma kodunu girin.", "code");
        }

        await TurnOffAsync(user);
        await audit.RecordAsync(user, AuditAction.TwoFactorDisabled, null, client, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(
        ModulixUser user, string? currentPassword, AccountClient client, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled)
        {
            throw Conflict("İki adımlı doğrulama açık değil.");
        }

        await EnsurePasswordAsync(user, currentPassword);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        await audit.RecordAsync(user, AuditAction.RecoveryCodesRegenerated, null, client, cancellationToken);
        return codes;
    }

    /// <summary>
    /// Girişteki ikinci adım: 6 haneli kod uygulamanınkidir, diğerleri kurtarma kodu sayılır (kullanılınca harcanır).
    /// Kurtarma koduyla girişte kişiye e-posta gider.
    /// </summary>
    public async Task<TwoFactorCodeResult> VerifySignInAsync(
        ModulixUser user, string? code, string publicUrl, AccountClient client, CancellationToken cancellationToken)
    {
        var result = await VerifyCodeAsync(user, code);
        if (result == TwoFactorCodeResult.RecoveryCode)
        {
            var left = await users.CountRecoveryCodesAsync(user);
            emails.Enqueue(UserEmails.RecoveryCodeUsed(user.Email!, user.DisplayName, left, AccountTokens.AccountLink(publicUrl)));
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(user, AuditAction.SignedInWithRecoveryCode, null, client, cancellationToken,
                details: new JsonObject { ["recoveryCodesLeft"] = left });
        }

        return result;
    }

    /// <summary>
    /// Platform yöneticisinin sıfırlaması (telefonunu ve kurtarma kodlarını kaybeden kişi): doğrulama kapanır, oturumlar
    /// kapanır. Zorunlu kılan firmaya girerken kişi kurulumu yeniden yapar. Hesap yoksa <c>false</c>.
    /// </summary>
    public async Task<bool> ResetAsync(string? email, Guid platformAdministratorId, AccountClient client, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return false;
        }

        if (!user.TwoFactorEnabled)
        {
            return true;
        }

        await TurnOffAsync(user);
        await sessions.CloseAllAsync(user.Id, except: null, cancellationToken);
        await audit.RecordAsync(user, AuditAction.TwoFactorReset, null, client, cancellationToken, actorId: platformAdministratorId);
        return true;
    }

    /// <summary>Kişinin etkin üye olduğu ve iki adımlı doğrulamayı zorunlu kılan firmaların adları.</summary>
    public async Task<IReadOnlyList<string>> RequiringTenantsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await (
            from membership in db.Memberships.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.UserId == userId && membership.Status == MembershipStatus.Active && TenantStatusRules.Operational.Contains(tenant.Status)
            select new { membership.Id, TenantId = tenant.Id, tenant.Name })
            .ToListAsync(cancellationToken);

        var requiring = new List<string>();
        foreach (var membership in memberships)
        {
            await using var scope = tenantScopes.Create(new TenantContext(membership.TenantId, userId, membership.Id));
            var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
            if (await settings.GetValueAsync(CoreModule.SettingKeys.RequireTwoFactor, cancellationToken) == CoreModule.SettingValues.Yes)
            {
                requiring.Add(membership.Name);
            }
        }

        return requiring;
    }

    private async Task<TwoFactorCodeResult> VerifyCodeAsync(ModulixUser user, string? code)
    {
        var normalized = Normalize(code);
        if (normalized.Length == 0)
        {
            return TwoFactorCodeResult.Invalid;
        }

        if (normalized.Length == 6 && normalized.All(char.IsAsciiDigit))
        {
            return await VerifyAuthenticatorAsync(user, normalized) ? TwoFactorCodeResult.Authenticator : TwoFactorCodeResult.Invalid;
        }

        // Kurtarma kodları "XXXXX-XXXXX" biçimindedir; kişi küçük harfle veya tiresiz yazabilir.
        var recovery = normalized.ToUpperInvariant();
        if (recovery.Length == 10)
        {
            recovery = $"{recovery[..5]}-{recovery[5..]}";
        }

        return (await users.RedeemTwoFactorRecoveryCodeAsync(user, recovery)).Succeeded
            ? TwoFactorCodeResult.RecoveryCode
            : TwoFactorCodeResult.Invalid;
    }

    private Task<bool> VerifyAuthenticatorAsync(ModulixUser user, string? code) =>
        Normalize(code) is { Length: 6 } normalized
            ? users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized)
            : Task.FromResult(false);

    // Kapalıyken eski anahtar ve kodlar geçersiz kalsın.
    private async Task TurnOffAsync(ModulixUser user)
    {
        ThrowIfFailed(await users.SetTwoFactorEnabledAsync(user, false));
        ThrowIfFailed(await users.ResetAuthenticatorKeyAsync(user));
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
    }

    private async Task EnsurePasswordAsync(ModulixUser user, string? currentPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            throw new ArgumentException("Mevcut şifrenizi girin.", "currentPassword");
        }

        if (!await users.CheckPasswordAsync(user, currentPassword))
        {
            await users.AccessFailedAsync(user);
            throw new ArgumentException("Mevcut şifre hatalı.", "currentPassword");
        }
    }

    private static string Normalize(string? code) =>
        string.Concat((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-'));

    // Uygulamaya elle girerken okunsun: dörderli gruplar, küçük harf.
    private static string FormatKey(string key) =>
        string.Join(' ', key.ToLowerInvariant().Chunk(4).Select(c => new string(c)));

    private static UserAdministrationException Conflict(string message) => new(UserAdministrationError.Conflict, message);

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
