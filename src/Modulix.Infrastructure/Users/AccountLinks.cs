using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Users;

public sealed record InvitationInfo(Guid MembershipId, Guid TenantId, string TenantName, string Email, string DisplayName);

/// <summary>
/// Bağlantı işleminin sonucu. <see cref="InvalidLink"/>: token geçersiz, süresi dolmuş, kullanılmış veya davet iptal
/// edilmiş (hangisi olduğu söylenmez). <see cref="PasswordErrors"/>: şifre kurallara uymuyor; bağlantı harcanmadı.
/// </summary>
public sealed record AccountLinkResult(ModulixUser? User, Guid? TenantId, bool InvalidLink, IReadOnlyList<string> PasswordErrors)
{
    public bool Succeeded => User is not null;

    /// <summary>Davet kabulünde etkinleşen üyeliklerin firmaları (kişinin bekleyen tüm davetleri).</summary>
    public IReadOnlyList<Guid> AcceptedTenantIds { get; init; } = [];

    public static AccountLinkResult Invalid { get; } = new(null, null, true, []);

    public static AccountLinkResult WeakPassword(IdentityResult result) =>
        new(null, null, false, result.Errors.Select(e => e.Description).ToList());
}

/// <summary>
/// E-postadaki davet ve şifre sıfırlama bağlantıları; oturum ve aktif tenant gerektirmez. Davet kabul edilince
/// kişinin bekleyen tüm davetleri etkinleşir: hepsi aynı e-posta adresine gitmiştir ve şifre belirlenince güvenlik
/// damgası değişip diğer davet bağlantıları geçersiz olur.
/// </summary>
public sealed class AccountLinks(ModulixDbContext db, UserManager<ModulixUser> users, UserSessions sessions)
{
    public async Task<InvitationInfo?> FindInvitationAsync(Guid membershipId, string? token, CancellationToken cancellationToken)
    {
        var invitation = await LoadInvitationAsync(membershipId, cancellationToken);
        if (invitation is null || string.IsNullOrEmpty(token) || !await IsValidInvitationAsync(invitation.Value.User, membershipId, token))
        {
            return null;
        }

        var (membership, user, tenantName) = invitation.Value;
        return new InvitationInfo(membership.Id, membership.TenantId, tenantName, user.Email ?? string.Empty, user.DisplayName);
    }

    public async Task<AccountLinkResult> AcceptInvitationAsync(
        Guid membershipId, string? token, string? password, CancellationToken cancellationToken)
    {
        var invitation = await LoadInvitationAsync(membershipId, cancellationToken);
        if (invitation is null || string.IsNullOrEmpty(token) || !await IsValidInvitationAsync(invitation.Value.User, membershipId, token))
        {
            return AccountLinkResult.Invalid;
        }

        var (membership, user, _) = invitation.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var added = await users.AddPasswordAsync(user, password ?? string.Empty);
        if (!added.Succeeded)
        {
            return AccountLinkResult.WeakPassword(added);
        }

        // Bağlantı e-postaya gittiği için adres doğrulanmış olur.
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);

        var pending = await db.Memberships.Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Invited).ToListAsync(cancellationToken);
        pending.ForEach(m => m.AcceptInvitation());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AccountLinkResult(user, membership.TenantId, false, []) { AcceptedTenantIds = [.. pending.Select(m => m.TenantId)] };
    }

    /// <summary>Şifreyi bağlantıyla değiştirir; hesap kilitliyse kilidi de açar. Oturum açmaz.</summary>
    public async Task<AccountLinkResult> ResetPasswordAsync(Guid userId, string? token, string? password, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || string.IsNullOrEmpty(token) || !await users.HasPasswordAsync(user))
        {
            return AccountLinkResult.Invalid;
        }

        // Şifre kurallara uymuyorsa bağlantı harcanmasın (ResetPasswordAsync önce token'ı doğrular).
        if (!await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, UserManager<ModulixUser>.ResetPasswordTokenPurpose, token))
        {
            return AccountLinkResult.Invalid;
        }

        var reset = await users.ResetPasswordAsync(user, token, password ?? string.Empty);
        if (!reset.Succeeded)
        {
            return reset.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? AccountLinkResult.Invalid
                : AccountLinkResult.WeakPassword(reset);
        }

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);

        // Şifre ele geçirilmiş olabilir: sıfırlamadan önce açılmış bütün oturumlar (saldırganınki dahil) hemen kapanır.
        await sessions.CloseAllAsync(user.Id, except: null, cancellationToken);
        return new AccountLinkResult(user, null, false, []);
    }

    private async Task<(Membership Membership, ModulixUser User, string TenantName)?> LoadInvitationAsync(
        Guid membershipId, CancellationToken cancellationToken)
    {
        var row = await (
            from membership in db.Memberships
            join tenant in db.Tenants.AsNoTracking() on membership.TenantId equals tenant.Id
            where membership.Id == membershipId
                  && membership.Status == MembershipStatus.Invited
                  && TenantStatusRules.Operational.Contains(tenant.Status)
            select new { Membership = membership, TenantName = tenant.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var user = await users.FindByIdAsync(row.Membership.UserId.ToString());
        return user is null || await users.HasPasswordAsync(user) ? null : (row.Membership, user, row.TenantName);
    }

    private Task<bool> IsValidInvitationAsync(ModulixUser user, Guid membershipId, string token) =>
        users.VerifyUserTokenAsync(user, AccountTokens.InvitationProvider, AccountTokens.InvitationPurpose(membershipId), token);
}
