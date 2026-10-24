using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Persistence;

namespace Modulix.Infrastructure.Users;

public sealed class UserAdministrationOptions
{
    public const string SectionName = "Users";

    /// <summary>Bir tenant'ın bir saatte gönderebileceği davet sayısı (kötüye kullanım sınırı).</summary>
    public int MaxInvitationsPerHour { get; set; } = 50;
}

internal sealed class UserAdministration(
    ModulixDbContext db,
    UserManager<ModulixUser> users,
    ITenantContextAccessor tenantAccessor,
    IRoleService roles,
    AdministratorGuard administrators,
    IEmailQueue emails,
    IAuditWriter audit,
    RecordHandover handover,
    MembershipPermissions membershipPermissions,
    IModuleEntitlements entitlements,
    UserSessions sessions,
    TimeProvider time,
    IOptions<UserAdministrationOptions> options) : IUserAdministration
{
    public const int MaxPageSize = 100;

    /// <summary>Aynı davetin yeniden gönderimi arasında beklenecek süre.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);

    public async Task<UserPage> ListAsync(UserQuery query, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var members =
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.TenantId == tenant.TenantId
            select new { Membership = membership, User = user };

        if (query.Search?.Trim() is { Length: > 0 } search)
        {
            members = members.Where(m => m.User.DisplayName.Contains(search) || m.User.Email!.Contains(search));
        }

        if (query.Status is { } status)
        {
            members = members.Where(m => m.Membership.Status == status);
        }

        if (query.TwoFactorEnabled is { } twoFactor)
        {
            members = members.Where(m => m.User.TwoFactorEnabled == twoFactor);
        }

        if (query.RoleId is { } roleId)
        {
            members = members.Where(m => db.MembershipRoles.Any(mr => mr.MembershipId == m.Membership.Id && mr.RoleId == roleId));
        }

        var total = await members.CountAsync(cancellationToken);
        var rows = await members
            .OrderBy(m => m.User.DisplayName).ThenBy(m => m.User.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var rolesByMembership = await LoadRolesAsync(rows.Select(r => r.Membership.Id).ToList(), cancellationToken);
        var items = rows.Select(r => ToUser(r.Membership, r.User, rolesByMembership, tenant)).ToList();
        return new UserPage(items, page, pageSize, total);
    }

    public async Task<TenantUser?> GetAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.Id == membershipId && membership.TenantId == tenant.TenantId
            select new { Membership = membership, User = user })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var rolesByMembership = await LoadRolesAsync([membershipId], cancellationToken);
        return ToUser(row.Membership, row.User, rolesByMembership, tenant);
    }

    public Task<InvitationResult> InviteAsync(UserInvitation invitation, CancellationToken cancellationToken) =>
        InviteCoreAsync(invitation, byPlatform: false, cancellationToken);

    /// <summary>
    /// Platform yöneticisinin daveti (Y4): aktif bağlam firmanın bağlamıdır ama işlemi yapan firmanın üyesi değildir,
    /// bu yüzden Yönetici rolünü vermek için yönetici olma kuralı uygulanmaz (platform API'si yetkiyi denetler).
    /// Açık bir işlem varsa ona katılır; firma açılışında davet de aynı işlemde oluşur.
    /// </summary>
    internal Task<InvitationResult> InviteAsPlatformAsync(UserInvitation invitation, CancellationToken cancellationToken) =>
        InviteCoreAsync(invitation, byPlatform: true, cancellationToken);

    private async Task<InvitationResult> InviteCoreAsync(UserInvitation invitation, bool byPlatform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var tenant = RequireTenant();
        var email = ValidateEmail(invitation.Email);
        var displayName = ValidateDisplayName(invitation.DisplayName);
        var roleIds = invitation.RoleIds.ToHashSet();

        var assignable = await db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.IsSystem })
            .ToListAsync(cancellationToken);
        var missing = roleIds.Except(assignable.Select(r => r.Id)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Bu kuruluşta bulunmayan rol: {string.Join(", ", missing)}.", "roleIds");
        }

        if (!byPlatform && assignable.Any(r => r.IsSystem))
        {
            await EnsureActorIsAdministratorAsync(tenant, cancellationToken);
        }

        var now = time.GetUtcNow();
        await EnsureInvitationAllowanceAsync(tenant, now, cancellationToken);

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var user = await users.FindByEmailAsync(email);
        if (user is not null
            && await db.Memberships.AnyAsync(m => m.TenantId == tenant.TenantId && m.UserId == user.Id, cancellationToken))
        {
            throw new UserAdministrationException(
                UserAdministrationError.Conflict, "Bu e-posta adresi kuruluşunuzda zaten kayıtlı.");
        }

        if (user is null)
        {
            user = new ModulixUser { UserName = email, Email = email, DisplayName = displayName, CreatedAt = now };
            ThrowIfFailed(await users.CreateAsync(user));
        }

        // Kayıtlı hesapta adres, hesabın kendi yazımıyla kullanılır (aramada büyük/küçük harf fark etmez).
        email = user.Email ?? email;

        // Şifresi olan hesap başka tenant'ta kullanılıyor: kişinin kimliği belli, üyelik hemen etkin olur.
        var existingAccount = await users.HasPasswordAsync(user);
        var membership = Membership.CreateInvited(tenant.TenantId, user.Id, awaitsAcceptance: !existingAccount, now);
        db.Memberships.Add(membership);
        foreach (var roleId in roleIds)
        {
            db.MembershipRoles.Add(new MembershipRole(membership.Id, roleId));
        }

        var context = await LoadEmailContextAsync(tenant, cancellationToken);
        if (existingAccount)
        {
            emails.Enqueue(UserEmails.AddedToTenant(
                email, user.DisplayName, context.TenantName, context.ActorName, AccountTokens.SignInLink(invitation.PublicUrl)));
        }
        else
        {
            var token = await users.GenerateUserTokenAsync(
                user, AccountTokens.InvitationProvider, AccountTokens.InvitationPurpose(membership.Id));
            emails.Enqueue(UserEmails.Invitation(
                email,
                displayName,
                context.TenantName,
                context.ActorName,
                AccountTokens.InvitationLink(invitation.PublicUrl, membership.Id, token)));
        }

        Audit(tenant, membership.Id, AuditAction.Created, before: null, after: new JsonObject
        {
            ["email"] = email,
            ["displayName"] = user.DisplayName,
            ["status"] = membership.Status.ToString(),
            ["roles"] = new JsonArray([.. assignable.Select(r => r.Name).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n))]),
        });

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var created = await GetAsync(membership.Id, cancellationToken);
        return new InvitationResult(created!, existingAccount);
    }

    public async Task ResendInvitationAsync(Guid membershipId, string publicUrl, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Status != MembershipStatus.Invited)
        {
            throw Conflict("Bu kullanıcının bekleyen daveti yok.");
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        var now = time.GetUtcNow();
        if (membership.InvitedAt is { } sentAt && now - sentAt < ResendCooldown)
        {
            throw new UserAdministrationException(
                UserAdministrationError.TooManyRequests, "Davet az önce gönderildi. Bir dakika sonra tekrar deneyin.");
        }

        await EnsureInvitationAllowanceAsync(tenant, now, cancellationToken);

        var token = await users.GenerateUserTokenAsync(
            user, AccountTokens.InvitationProvider, AccountTokens.InvitationPurpose(membership.Id));

        var before = membership.InvitedAt;
        membership.MarkInvitationSent(now);
        var context = await LoadEmailContextAsync(tenant, cancellationToken);
        emails.Enqueue(UserEmails.Invitation(
            user.Email!,
            user.DisplayName,
            context.TenantName,
            context.ActorName,
            AccountTokens.InvitationLink(publicUrl, membership.Id, token)));
        Audit(tenant, membership.Id, AuditAction.Updated,
            new JsonObject { ["invitedAt"] = before }, new JsonObject { ["invitedAt"] = now });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelInvitationAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Status != MembershipStatus.Invited)
        {
            throw Conflict("Bu kullanıcının bekleyen daveti yok. Etkin kullanıcıyı devre dışı bırakabilirsiniz.");
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        // Davet bekleyen üyelikte kayıt yoktur (sorumlu olamaz, giriş yapamaz); satır silinir ve bağlantı, üyeliğe bağlı
        // olduğu için çalışmaz. Hesap kalır: kişi başka tenant'ın davetini bekliyor olabilir; yeniden davet edilirse
        // aynı hesap kullanılır (yeni üyelik, yeni bağlantı).
        db.MembershipRoles.RemoveRange(await db.MembershipRoles.Where(mr => mr.MembershipId == membershipId).ToListAsync(cancellationToken));
        db.Memberships.Remove(membership);
        Audit(tenant, membershipId, AuditAction.Deleted,
            new JsonObject { ["email"] = user.Email, ["status"] = membership.Status.ToString() }, after: null);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DisableAsync(Guid membershipId, Guid? transferToMembershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, _) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Id == tenant.MembershipId)
        {
            throw Conflict("Kendi hesabınızı devre dışı bırakamazsınız.");
        }

        if (membership.Status == MembershipStatus.Invited)
        {
            throw Conflict("Davet bekleyen kullanıcı devre dışı bırakılamaz; daveti iptal edin.");
        }

        if (membership.Status == MembershipStatus.Disabled)
        {
            if (transferToMembershipId is { } target)
            {
                await TransferRecordsAsync(membershipId, target, cancellationToken);
            }

            return;
        }

        var receiver = transferToMembershipId is { } to ? await LoadReceiverAsync(tenant, membership, to, cancellationToken) : null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Yönetici kuralları kilit altında: aynı anda birbirini devre dışı bırakan iki yönetici sırayla işlenir.
        var systemRoleId = await administrators.LockAsync(cancellationToken);
        if (systemRoleId is { } systemId
            && await db.MembershipRoles.AnyAsync(mr => mr.MembershipId == membershipId && mr.RoleId == systemId, cancellationToken))
        {
            await administrators.EnsureIsAdministratorAsync(systemId, tenant.MembershipId, cancellationToken);
            await administrators.EnsureAnotherActiveAdministratorAsync(systemId, membershipId, cancellationToken);
        }

        // Kilit beklenirken başka bir istek durumu değiştirmiş olabilir.
        await db.Entry(membership).ReloadAsync(cancellationToken);
        if (membership.Status != MembershipStatus.Active)
        {
            return;
        }

        // Devir aynı işlemde: kayıtlar devredilmeden kişi devre dışı kalmaz, devre dışı kalmadan kayıtlar el değiştirmez.
        if (receiver is not null)
        {
            await handover.TransferAsync(membership.UserId, receiver.UserId, cancellationToken);
        }

        membership.Disable();
        AuditStatus(tenant, membership, MembershipStatus.Active);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OwnedRecordSummary>> GetOwnedRecordsAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, _) = await LoadAsync(tenant, membershipId, cancellationToken);
        return await handover.CountAsync(membership.UserId, cancellationToken);
    }

    public async Task<UserAccess> GetAccessAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        await LoadAsync(tenant, membershipId, cancellationToken);
        var access = await membershipPermissions.GetAsync(
            membershipId, await entitlements.GetEnabledAsync(tenant.TenantId, cancellationToken), cancellationToken);

        var grants = access.Permissions
            .Order(StringComparer.Ordinal)
            .Select(p => new PermissionGrant(p, access.RolesGranting(p).Select(r => new UserRole(r.Id, r.Name, r.IsSystem)).ToList()))
            .ToList();
        var teams = await (
            from member in db.TeamMembers.AsNoTracking()
            join team in db.Teams.AsNoTracking() on member.TeamId equals team.Id
            where member.MembershipId == membershipId
            orderby team.Name
            select team.Name)
            .ToListAsync(cancellationToken);
        return new UserAccess(access.IsAdministrator, grants) { Scope = access.Scope, Teams = teams };
    }

    public async Task<int> TransferRecordsAsync(Guid membershipId, Guid toMembershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, _) = await LoadAsync(tenant, membershipId, cancellationToken);
        var receiver = await LoadReceiverAsync(tenant, membership, toMembershipId, cancellationToken);
        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var moved = await handover.TransferAsync(membership.UserId, receiver.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return moved;
    }

    public async Task EnableAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, _) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Status == MembershipStatus.Invited)
        {
            throw Conflict("Davet bekleyen kullanıcı daveti kabul edince etkinleşir.");
        }

        if (membership.Status == MembershipStatus.Active)
        {
            return;
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        membership.Enable();
        AuditStatus(tenant, membership, MembershipStatus.Disabled);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SendPasswordResetAsync(Guid membershipId, string publicUrl, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Status != MembershipStatus.Active)
        {
            throw Conflict(membership.Status == MembershipStatus.Invited
                ? "Kullanıcı henüz şifresini belirlemedi; daveti yeniden gönderin."
                : "Devre dışı kullanıcıya şifre sıfırlama bağlantısı gönderilemez.");
        }

        if (!await users.HasPasswordAsync(user))
        {
            throw Conflict("Kullanıcının şifresi yok; önce daveti kabul etmeli.");
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var context = await LoadEmailContextAsync(tenant, cancellationToken);
        emails.Enqueue(UserEmails.PasswordReset(
            user.Email!,
            user.DisplayName,
            context.TenantName,
            context.ActorName,
            AccountTokens.PasswordResetLink(publicUrl, user.Id, token)));
        Audit(tenant, membershipId, AuditAction.Updated,
            new JsonObject { ["passwordResetRequestedAt"] = null }, new JsonObject { ["passwordResetRequestedAt"] = time.GetUtcNow() });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CloseSessionsAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, _) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (membership.Status == MembershipStatus.Invited)
        {
            throw Conflict("Davet bekleyen kullanıcının oturumu yok.");
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await sessions.CloseAllAsync(membership.UserId, except: null, cancellationToken);
        Audit(tenant, membershipId, AuditAction.Updated,
            new JsonObject { ["sessionsClosedAt"] = null }, new JsonObject { ["sessionsClosedAt"] = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UnlockAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (_, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        if (!IsLockedOut(user))
        {
            return;
        }

        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ThrowIfFailed(await users.SetLockoutEndDateAsync(user, null));
        ThrowIfFailed(await users.ResetAccessFailedCountAsync(user));
        Audit(tenant, membershipId, AuditAction.Updated,
            new JsonObject { ["lockedOut"] = true }, new JsonObject { ["lockedOut"] = false });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetRolesAsync(Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        if (!await roles.SetMembershipRolesAsync(membershipId, roleIds, cancellationToken, actingMembershipId: tenant.MembershipId))
        {
            throw NotFound();
        }
    }

    private TenantContext RequireTenant() =>
        tenantAccessor.Current ?? throw new InvalidOperationException("Aktif tenant olmadan kullanıcılar yönetilemez.");

    // Üyelikler platform tablosundadır (RLS dışı); tenant'a ait olduğu burada açıkça denetlenir.
    private async Task<(Membership Membership, ModulixUser User)> LoadAsync(
        TenantContext tenant, Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships
            .SingleOrDefaultAsync(m => m.Id == membershipId && m.TenantId == tenant.TenantId, cancellationToken)
            ?? throw NotFound();
        var user = await users.FindByIdAsync(membership.UserId.ToString()) ?? throw NotFound();
        return (membership, user);
    }

    /// <summary>Kayıtları devralacak üye: bu tenant'ta etkin olmalı ve devreden kişi olmamalı.</summary>
    private async Task<Membership> LoadReceiverAsync(TenantContext tenant, Membership from, Guid toMembershipId, CancellationToken cancellationToken)
    {
        if (toMembershipId == from.Id)
        {
            throw new ArgumentException("Kayıtlar aynı kişiye devredilemez.", "transferToMembershipId");
        }

        var receiver = await db.Memberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == toMembershipId && m.TenantId == tenant.TenantId, cancellationToken);
        return receiver is { Status: MembershipStatus.Active }
            ? receiver
            : throw new ArgumentException("Kayıtları devralacak kişi bu kuruluşun etkin bir üyesi olmalı.", "transferToMembershipId");
    }

    private async Task<Dictionary<Guid, List<UserRole>>> LoadRolesAsync(IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken)
    {
        var assignments = await (
            from assignment in db.MembershipRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            where membershipIds.Contains(assignment.MembershipId)
            orderby role.Name
            select new { assignment.MembershipId, Role = new UserRole(role.Id, role.Name, role.IsSystem) })
            .ToListAsync(cancellationToken);

        return assignments.GroupBy(a => a.MembershipId).ToDictionary(g => g.Key, g => g.Select(a => a.Role).ToList());
    }

    /// <summary>Hedef üyelik yöneticiyse işlemi yalnızca yönetici yapabilir.</summary>
    private async Task EnsureMayManageAsync(TenantContext tenant, Guid membershipId, CancellationToken cancellationToken)
    {
        if (await IsAdministratorAsync(membershipId, cancellationToken))
        {
            await EnsureActorIsAdministratorAsync(tenant, cancellationToken);
        }
    }

    private async Task EnsureActorIsAdministratorAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        if (!await IsAdministratorAsync(tenant.MembershipId, cancellationToken))
        {
            throw new UserAdministrationException(UserAdministrationError.AdministratorRequired, AdministratorGuard.AdministratorRequiredMessage);
        }
    }

    private Task<bool> IsAdministratorAsync(Guid membershipId, CancellationToken cancellationToken) =>
        (from assignment in db.MembershipRoles
         join role in db.Roles on assignment.RoleId equals role.Id
         where assignment.MembershipId == membershipId && role.IsSystem
         select assignment.Id)
        .AnyAsync(cancellationToken);

    private async Task EnsureInvitationAllowanceAsync(TenantContext tenant, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - TimeSpan.FromHours(1);
        var sent = await db.Memberships.CountAsync(m => m.TenantId == tenant.TenantId && m.InvitedAt > since, cancellationToken);
        if (sent >= options.Value.MaxInvitationsPerHour)
        {
            throw new UserAdministrationException(
                UserAdministrationError.TooManyRequests,
                $"Bir saatte en fazla {options.Value.MaxInvitationsPerHour} davet gönderilebilir. Biraz sonra tekrar deneyin.");
        }
    }

    private async Task<(string TenantName, string ActorName)> LoadEmailContextAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var actorName = await db.Users.AsNoTracking().Where(u => u.Id == tenant.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);
        return (tenantName, actorName ?? "Kuruluşunuzun yöneticisi");
    }

    private bool IsLockedOut(ModulixUser user) => user.LockoutEnd is { } end && end > time.GetUtcNow();

    private TenantUser ToUser(Membership membership, ModulixUser user, Dictionary<Guid, List<UserRole>> rolesByMembership, TenantContext tenant) =>
        new(
            membership.Id,
            user.Id,
            user.DisplayName,
            user.Email ?? string.Empty,
            membership.Status,
            rolesByMembership.GetValueOrDefault(membership.Id) ?? [],
            membership.CreatedAt,
            membership.InvitedAt,
            membership.LastSignInAt,
            IsLockedOut(user),
            membership.Id == tenant.MembershipId)
        {
            TwoFactorEnabled = user.TwoFactorEnabled,
        };

    private void AuditStatus(TenantContext tenant, Membership membership, MembershipStatus before) =>
        Audit(tenant, membership.Id, AuditAction.Updated,
            new JsonObject { ["status"] = before.ToString() }, new JsonObject { ["status"] = membership.Status.ToString() });

    private void Audit(TenantContext tenant, Guid membershipId, AuditAction action, JsonObject? before, JsonObject? after) =>
        audit.Add(new AuditEntry(
            tenant.TenantId,
            tenant.UserId,
            RoleService.MembershipEntityKey,
            membershipId,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    internal static string ValidateEmail(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("E-posta zorunludur.", "email");
        }

        if (trimmed.Length > 256
            || !MailAddress.TryCreate(trimmed, out var address)
            || address.Address != trimmed
            || !string.IsNullOrEmpty(address.DisplayName))
        {
            throw new ArgumentException("Geçerli bir e-posta adresi girin.", "email");
        }

        return trimmed;
    }

    internal static string ValidateDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Ad soyad zorunludur.", "displayName");
        }

        if (trimmed.Length > ModulixUser.DisplayNameMaxLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"Ad soyad en fazla {ModulixUser.DisplayNameMaxLength} karakter olabilir ve satır sonu içeremez.", "displayName");
        }

        return trimmed;
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    private static UserAdministrationException NotFound() =>
        new(UserAdministrationError.NotFound, "Kullanıcı bulunamadı.");

    private static UserAdministrationException Conflict(string message) =>
        new(UserAdministrationError.Conflict, message);
}
