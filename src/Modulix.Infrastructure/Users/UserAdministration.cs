using System.Diagnostics;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Identity;
using Modulix.Infrastructure.Organization;
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
    IBusinessUnitService businessUnits,
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
            // Rol atamaları RLS altındadır; alt sorgu aynı veritabanında çalışır (ADR-0020).
            members = members.Where(m => db.MembershipRoles.Any(mr => mr.RoleId == roleId && mr.MembershipId == m.Membership.Id));
        }

        if (query.BusinessUnitId is { } businessUnitId)
        {
            // Başka bayinin departmanına göre süzmek hiçbir şey döndürmez; üyelerinin kim olduğu bu yolla da öğrenilemez (ADR-0026).
            if (await membershipPermissions.GetVisibleUnitIdsAsync(tenant.MembershipId, cancellationToken) is { } visibleUnits
                && !visibleUnits.Contains(businessUnitId))
            {
                return new UserPage([], page, pageSize, 0);
            }

            // Departman üyelikleri RLS altındadır; alt sorgu aynı veritabanında çalışır (ADR-0020).
            members = members.Where(m => db.BusinessUnitMembers.Any(bm => bm.BusinessUnitId == businessUnitId && bm.MembershipId == m.Membership.Id));
        }

        var total = await members.CountAsync(cancellationToken);
        var rows = await members
            .OrderBy(m => m.User.DisplayName).ThenBy(m => m.User.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var rolesByMembership = await LoadRolesAsync(rows.Select(r => r.Membership.Id).ToList(), cancellationToken);
        var unitNames = await LoadBusinessUnitNamesAsync(rows.Select(r => r.Membership.Id).ToList(), cancellationToken);
        var items = rows
            .Select(r => ToUser(r.Membership, r.User, rolesByMembership, tenant) with { BusinessUnitName = unitNames.GetValueOrDefault(r.Membership.Id) })
            .ToList();
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
        var unitNames = await LoadBusinessUnitNamesAsync([membershipId], cancellationToken);
        return ToUser(row.Membership, row.User, rolesByMembership, tenant) with { BusinessUnitName = unitNames.GetValueOrDefault(membershipId) };
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

        var businessUnit = await ResolveInviteBusinessUnitAsync(tenant, invitation.BusinessUnitId, byPlatform, cancellationToken);
        var now = time.GetUtcNow();
        await EnsureInvitationAllowanceAsync(tenant, now, cancellationToken);

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

        // Üyelik, rol atamaları ve denetim kaydı tek işlemde yazılır (ADR-0020): hata olursa rolsüz yetim üyelik kalmaz.
        // Açık bir işlem varsa (firma açılışı) ona katılır. EF ekleme sırasını yabancı anahtara göre kendisi belirler.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        db.Memberships.Add(membership);
        foreach (var roleId in roleIds)
        {
            db.MembershipRoles.Add(new MembershipRole(membership.Id, roleId));
        }

        db.BusinessUnitMembers.Add(new BusinessUnitMember(businessUnit.Id, membership.Id));

        Audit(tenant, membership.Id, AuditAction.Created, before: null, after: new JsonObject
        {
            ["email"] = email,
            ["displayName"] = user.DisplayName,
            ["status"] = membership.Status.ToString(),
            ["roles"] = new JsonArray([.. assignable.Select(r => r.Name).Order(StringComparer.Ordinal).Select(n => JsonValue.Create(n))]),
            ["businessUnit"] = businessUnit.Name,
        });

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
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
        // Rol atamaları, denetim kaydı ve üyelik tek kaydetmede silinir (ADR-0020); EF silme sırasını yabancı anahtara göre belirler.
        db.MembershipRoles.RemoveRange(await db.MembershipRoles.Where(mr => mr.MembershipId == membershipId).ToListAsync(cancellationToken));
        Audit(tenant, membershipId, AuditAction.Deleted,
            new JsonObject { ["email"] = user.Email, ["status"] = membership.Status.ToString() }, after: null);
        db.Memberships.Remove(membership);
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

        // Devir, üyeliğin kapatılması ve denetim kaydı tek işlemdedir (ADR-0020). Önce kayıtlar devredilir, sonra üyelik kapatılır.
        if (receiver is not null)
        {
            await handover.TransferAsync(membership.UserId, receiver.UserId, cancellationToken);
        }

        // Durum değişikliği önce bellekte uygulanır ki denetim kaydı yeni durumu yazsın.
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
            .Select(p => new PermissionGrant(
                p, access.RolesGranting(p).Select(r => new UserRole(r.Id, r.Name, r.IsSystem) { ViaBusinessUnit = r.ViaBusinessUnit }).ToList()))
            .ToList();
        var unit = await membershipPermissions.GetUnitAsync(membershipId, cancellationToken);
        var teams = await (
            from member in db.TeamMembers.AsNoTracking()
            join team in db.Teams.AsNoTracking() on member.TeamId equals team.Id
            where member.MembershipId == membershipId
            orderby team.Name
            select team.Name)
            .ToListAsync(cancellationToken);
        return new UserAccess(access.IsAdministrator, grants)
        {
            Scope = access.Scope,
            Teams = teams,
            BusinessUnit = unit?.Name,
            Scopes = access.Scopes,
            Roles = access.Roles.Select(r => new UserRoleSource(r.Id, r.Name, r.IsSystem, r.Scope, r.ViaBusinessUnit)).ToList(),
        };
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

    public async Task<BusinessUnitChangePreview> PreviewBusinessUnitChangeAsync(
        Guid membershipId, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var tenant = RequireTenant();
        var (membership, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);
        var context = await LoadChangeContextAsync(tenant, membership, businessUnitId, cancellationToken);
        return await BuildChangePreviewAsync(membership, user, context, cancellationToken);
    }

    public async Task<int> ChangeBusinessUnitAsync(Guid membershipId, BusinessUnitChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tenant = RequireTenant();
        var (membership, user) = await LoadAsync(tenant, membershipId, cancellationToken);
        await EnsureMayManageAsync(tenant, membershipId, cancellationToken);
        var context = await LoadChangeContextAsync(tenant, membership, change.BusinessUnitId, cancellationToken);
        if (!context.IsChange)
        {
            return 0;
        }

        if (change.MoveRecordsWithUser && change.TransferRecordsToMembershipId is not null)
        {
            throw new ArgumentException(
                "Kayıtlar ya kullanıcıyla birlikte taşınır ya da devredilir; ikisi birlikte seçilemez.", "transferRecordsToMembershipId");
        }

        var preview = await BuildChangePreviewAsync(membership, user, context, cancellationToken);
        var reason = change.Reason?.Trim();
        if (reason?.Length > ChangeReasonMaxLength)
        {
            throw new ArgumentException($"Gerekçe en fazla {ChangeReasonMaxLength} karakter olabilir.", "reason");
        }

        if (preview.DecisionRequired && !change.MoveRecordsWithUser && change.TransferRecordsToMembershipId is null)
        {
            throw Conflict(
                $"Bu kullanıcının {preview.OwnedTotal} kaydı var{(preview.OwnedUnknown ? " (göremediğiniz kayıt türleri de olabilir)" : string.Empty)}. "
                + "Departman değişince kayıtlar yeni departmanla görünür: kayıtları eski departmandaki bir kullanıcıya devredin "
                + "ya da kayıtların kullanıcıyla birlikte taşınacağını onaylayın.");
        }

        if (preview.ReasonRequired && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Bu değişiklik için gerekçe zorunludur.", "reason");
        }

        var receiver = change.TransferRecordsToMembershipId is { } to
            ? await LoadStayingReceiverAsync(tenant, membership, context, to, cancellationToken)
            : null;
        var receiverName = receiver is null ? null : (await users.FindByIdAsync(receiver.UserId.ToString()))?.DisplayName;

        // Devir, taşıma ve denetim kaydı tek işlemdedir: devir başarısız olursa kullanıcı eski departmanında kalır.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var transferred = receiver is null ? 0 : await handover.TransferAsync(membership.UserId, receiver.UserId, cancellationToken);
        var note = receiver is not null
            ? $"{transferred} kayıt {receiverName} kişisine devredildi (eski departmanda kaldı)"
            : preview.DecisionRequired ? $"{preview.OwnedTotal} kayıt kullanıcıyla birlikte yeni departmana taşındı" : null;
        if (!await businessUnits.SetMembershipBusinessUnitAsync(membershipId, change.BusinessUnitId, cancellationToken, reason, note))
        {
            throw NotFound();
        }

        await transaction.CommitAsync(cancellationToken);
        return transferred;
    }

    private const int ChangeReasonMaxLength = 500;

    private sealed record ChangeContext(MembershipUnit? Current, string? CurrentPathNames, BusinessUnit Target, string TargetPathNames)
    {
        public bool IsChange => Current?.Id != Target.Id;
    }

    /// <summary>
    /// Taşımanın tarafları: hedef departman bu tenant'ta olmalı ve etkin olmalı; yönetici olmayan işlemi yapan yalnızca kendi
    /// kapsamındaki departmanlar arasında taşıyabilir (ADR-0026 K6): "departman ve altı" kendi ağacını, diğerleri yalnızca kendi
    /// departmanını kapsar. Taşıma görünürlüğü değiştirdiği için bu sınır önizlemede de uygulanır.
    /// </summary>
    private async Task<ChangeContext> LoadChangeContextAsync(
        TenantContext tenant, Membership membership, Guid businessUnitId, CancellationToken cancellationToken)
    {
        var target = await db.BusinessUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == businessUnitId, cancellationToken)
            ?? throw new ArgumentException("Bu firmada bulunmayan departman seçildi.", "businessUnitId");
        if (!target.IsActive)
        {
            throw new ArgumentException("Kapalı bir departmana kullanıcı taşınamaz.", "businessUnitId");
        }

        var current = await membershipPermissions.GetUnitAsync(membership.Id, cancellationToken);
        if (await membershipPermissions.GetVisibleUnitIdsAsync(tenant.MembershipId, cancellationToken) is { } allowed
            && ((current is not null && !allowed.Contains(current.Id)) || !allowed.Contains(target.Id)))
        {
            throw new UserAdministrationException(
                UserAdministrationError.PermissionDenied,
                "Kullanıcıyı yalnızca kendi departmanınız ve kapsamınızdaki departmanlar arasında taşıyabilirsiniz.");
        }

        var targetNames = (await businessUnits.FindAsync(target.Id, cancellationToken))?.PathNames ?? target.Name;
        var currentNames = current is null ? null : (await businessUnits.FindAsync(current.Id, cancellationToken))?.PathNames ?? current.Name;
        return new ChangeContext(current, currentNames, target, targetNames);
    }

    private async Task<BusinessUnitChangePreview> BuildChangePreviewAsync(
        Membership membership, ModulixUser user, ChangeContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<OwnedRecordSummary> owned = context.IsChange ? await handover.CountAsync(membership.UserId, cancellationToken) : Array.Empty<OwnedRecordSummary>();
        var total = owned.Sum(o => o.Count ?? 0);
        var unknown = owned.Any(o => o.Count is null);
        var decision = context.IsChange && (total > 0 || unknown);
        var reason = context.IsChange && (decision || membership.Status != MembershipStatus.Active);

        // Departmandan gelen roller değişir; kullanıcıya doğrudan verilenler korunur.
        var lost = new List<string>();
        var gained = new List<string>();
        if (context.IsChange)
        {
            var direct = (await db.MembershipRoles.AsNoTracking().Where(mr => mr.MembershipId == membership.Id).Select(mr => mr.RoleId).ToListAsync(cancellationToken)).ToHashSet();
            IReadOnlyList<(Guid Id, string Name)> before = context.Current is null ? Array.Empty<(Guid Id, string Name)>() : await membershipPermissions.GetUnitRolesAsync(context.Current.Id, context.Current.Path, cancellationToken);
            var after = await membershipPermissions.GetUnitRolesAsync(context.Target.Id, context.Target.Path, cancellationToken);
            lost.AddRange(before.Where(b => after.All(a => a.Id != b.Id) && !direct.Contains(b.Id)).Select(b => b.Name));
            gained.AddRange(after.Where(a => before.All(b => b.Id != a.Id) && !direct.Contains(a.Id)).Select(a => a.Name));
        }

        var warnings = new List<string>();
        if (context.IsChange && total > 0)
        {
            warnings.Add($"{total} kayıt kullanıcıyla birlikte yeni departmana geçer: eski departmandaki iş arkadaşları bunları artık görmez, yeni departman görür. "
                + "Kayıtları eski departmanda tutmak için bir iş arkadaşına devredin.");
        }

        if (unknown)
        {
            warnings.Add("Bazı kayıt türlerini görme yetkiniz olmadığı için kayıt sayısı eksik olabilir.");
        }

        if (membership.Status == MembershipStatus.Disabled)
        {
            warnings.Add("Kullanıcı devre dışı. Kayıtları bulunduğu departmanda görünmeye devam eder; taşırsanız yeni departmanla görünür.");
        }

        if (lost.Count > 0)
        {
            warnings.Add($"Departmandan gelen şu roller kalkar: {string.Join(", ", lost)}.");
        }

        if (gained.Count > 0)
        {
            warnings.Add($"Yeni departmandan şu roller gelir: {string.Join(", ", gained)}.");
        }

        return new BusinessUnitChangePreview(
            membership.Id,
            user.DisplayName,
            membership.Status,
            context.Current?.Id,
            context.CurrentPathNames,
            context.Target.Id,
            context.TargetPathNames,
            context.IsChange,
            owned,
            total,
            unknown,
            decision,
            reason,
            lost,
            gained,
            warnings);
    }

    /// <summary>
    /// Kayıtları devralacak üye: etkin olmalı, devreden kişi olmamalı ve kayıtlar eski departmanda kalsın diye o departmanda olmalı.
    /// </summary>
    private async Task<Membership> LoadStayingReceiverAsync(
        TenantContext tenant, Membership from, ChangeContext context, Guid toMembershipId, CancellationToken cancellationToken)
    {
        const string field = "transferRecordsToMembershipId";
        if (toMembershipId == from.Id)
        {
            throw new ArgumentException("Kayıtlar aynı kişiye devredilemez.", field);
        }

        var receiver = await db.Memberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == toMembershipId && m.TenantId == tenant.TenantId, cancellationToken);
        if (receiver is not { Status: MembershipStatus.Active })
        {
            throw new ArgumentException("Kayıtları devralacak kişi bu kuruluşun etkin bir üyesi olmalı.", field);
        }

        var currentId = context.Current?.Id;
        if (currentId is null || !await db.BusinessUnitMembers.AnyAsync(m => m.MembershipId == toMembershipId && m.BusinessUnitId == currentId, cancellationToken))
        {
            throw new ArgumentException("Kayıtları, kullanıcının eski departmanında kalan bir üyeye devredin.", field);
        }

        return receiver;
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

    /// <summary>Davetin departmanı: verilmişse etkin olmalı, verilmemişse kök departman (ADR-0026 K4).</summary>
    private async Task<BusinessUnit> ResolveInviteBusinessUnitAsync(TenantContext tenant, Guid? businessUnitId, bool byPlatform, CancellationToken cancellationToken)
    {
        // Yönetici olmayan davet eden yalnızca kendi kapsamındaki departmanlara kullanıcı ekleyebilir; kapsam dışı departman
        // "bulunmayan" ile aynı iletiyi alır. Departman verilmezse kök yerine kendi departmanı kullanılır.
        var visible = byPlatform ? null : await membershipPermissions.GetVisibleUnitIdsAsync(tenant.MembershipId, cancellationToken);
        if (businessUnitId is { } id)
        {
            return await db.BusinessUnits.SingleOrDefaultAsync(
                       u => u.Id == id && u.IsActive && (visible == null || visible.Contains(u.Id)), cancellationToken)
                ?? throw new ArgumentException("Bu kuruluşta bulunmayan ya da kapalı bir departman seçildi.", "businessUnitId");
        }

        if (visible is not null)
        {
            var own = await membershipPermissions.GetUnitAsync(tenant.MembershipId, cancellationToken)
                ?? throw new ArgumentException("Davet için bir departman seçin.", "businessUnitId");
            return await db.BusinessUnits.SingleAsync(u => u.Id == own.Id, cancellationToken);
        }

        var tenantName = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        return await DefaultBusinessUnit.EnsureRootAsync(db, tenantName, cancellationToken);
    }

    /// <summary>
    /// Kullanıcıların departman adları. Yönetici olmayan işlemi yapan başka bayinin departman adını göremez (ADR-0026): o
    /// kullanıcılar için ad boş döner.
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadBusinessUnitNamesAsync(IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken)
    {
        var visible = await membershipPermissions.GetVisibleUnitIdsAsync(RequireTenant().MembershipId, cancellationToken);
        var rows = await (
            from member in db.BusinessUnitMembers.AsNoTracking()
            join unit in db.BusinessUnits.AsNoTracking() on member.BusinessUnitId equals unit.Id
            where membershipIds.Contains(member.MembershipId)
            select new { member.MembershipId, UnitId = unit.Id, unit.Name })
            .ToListAsync(cancellationToken);
        return rows.Where(r => visible is null || visible.Contains(r.UnitId)).ToDictionary(r => r.MembershipId, r => r.Name);
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

    /// <summary>Firma adı ve işlemi yapanın adı tek sorguda (ADR-0020); yapan firmanın üyesi değilse (platform yöneticisi) genel ad kullanılır.</summary>
    private async Task<(string TenantName, string ActorName)> LoadEmailContextAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        var context = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant.TenantId)
            .Select(t => new
            {
                t.Name,
                ActorName = db.Users.Where(u => u.Id == tenant.UserId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .SingleAsync(cancellationToken);
        return (context.Name, context.ActorName ?? "Kuruluşunuzun yöneticisi");
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
