using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Notifications;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Audit;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;

namespace Modulix.Infrastructure.Platform;

/// <summary>
/// Destek erişimi (ADR-0028 Dilim 7, Y1.3). Erişim, firmada <see cref="MembershipStatus.Support"/> üyeliği ve salt okunur bir rolle
/// kurulur; bağlam yalnızca geçerli kayıt varken oluşur (<c>MembershipDirectory</c>), böylece süre dolumu bir işçiye bağlı değildir.
/// </summary>
internal sealed class SupportAccessService(
    ModulixDbContext db,
    ModuleCatalog catalog,
    ITenantScopeFactory tenantScopes,
    TimeProvider time) : ISupportAccess
{
    /// <summary>Firmada oluşturulan salt okunur rolün adı (firma yöneticisi rolü listesinde görünür).</summary>
    public const string RoleName = "Destek erişimi (salt okunur)";

    public async Task<SupportAccessInfo> GrantAsync(Guid tenantId, Guid userId, string reason, int minutes, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Firma bulunamadı.");
        if (!TenantStatusRules.IsOperational(tenant.Status))
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Kapalı ya da askıdaki firmaya destek erişimi açılamaz.");
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.IsPlatformAdministrator)
            .Select(u => new { u.Id, u.DisplayName })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Platform hesabı bulunamadı.");

        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, cancellationToken);
        if (membership is { Status: not MembershipStatus.Support })
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Kişi bu firmanın üyesi; ayrıca destek erişimi gerekmez.");
        }

        if (membership is not null
            && await db.SupportAccessGrants.AnyAsync(g => g.MembershipId == membership.Id && g.EndedAt == null && g.ExpiresAt > now, cancellationToken))
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Kişinin bu firmada zaten geçerli bir destek erişimi var.");
        }

        // Gerekçe ve süre doğrulanır (kayıt oluşturulurken); hata durumunda hiçbir şey yazılmaz.
        var membershipId = membership?.Id ?? Guid.CreateVersion7();
        var grant = new SupportAccessGrant(tenantId, userId, membershipId, reason, now, minutes);

        if (membership is null)
        {
            membership = Membership.CreateSupport(tenantId, userId);
            db.Memberships.Add(membership);
            await db.SaveChangesAsync(cancellationToken); // rol ataması bileşik yabancı anahtarla üyeliğe bağlıdır
            grant = new SupportAccessGrant(tenantId, userId, membership.Id, reason, now, minutes);
        }

        await using (var scope = tenantScopes.Create(new TenantContext(tenantId, userId, Guid.Empty)))
        {
            var services = scope.ServiceProvider;
            var tenantDb = services.GetRequiredService<ModulixDbContext>();
            var roleId = await EnsureRoleAsync(services, tenantId, cancellationToken);
            await services.GetRequiredService<IRoleService>().SetMembershipRolesAsync(membership.Id, [roleId], cancellationToken);

            // Kişi firmanın kök departmanına eklenir (diğer üyeler gibi); kayıt kapsamı rolden (tümü) gelir.
            var root = await tenantDb.BusinessUnits.AsNoTracking().FirstOrDefaultAsync(u => u.ParentId == null, cancellationToken);
            if (root is not null && !await tenantDb.BusinessUnitMembers.AnyAsync(m => m.MembershipId == membership.Id, cancellationToken))
            {
                tenantDb.BusinessUnitMembers.Add(new BusinessUnitMember(root.Id, membership.Id));
            }

            AuditTenant(services, tenant, userId, before: new JsonObject { ["supportAccess"] = null },
                after: new JsonObject { ["supportAccess"] = Describe(user.DisplayName, grant) });
            await NotifyAdministratorsAsync(services, tenantDb, tenant.Name, user.DisplayName, grant, cancellationToken);
            await tenantDb.SaveChangesAsync(cancellationToken);
        }

        db.SupportAccessGrants.Add(grant);
        db.PlatformAuditLog.Add(new PlatformAuditEntry(
            userId, PlatformAuditActions.SupportAccessGranted, tenantId, grant.Reason,
            new JsonObject { ["grantId"] = grant.Id, ["expiresAt"] = grant.ExpiresAt.ToString("O") }.ToJsonString(), Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);
        return ToInfo(grant, tenant.Name, user.DisplayName, now);
    }

    public async Task RevokeAsync(Guid grantId, Guid? tenantId, Guid actingUserId, string? reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var grant = await db.SupportAccessGrants.SingleOrDefaultAsync(g => g.Id == grantId && (tenantId == null || g.TenantId == tenantId), cancellationToken)
            ?? throw new UserAdministrationException(UserAdministrationError.NotFound, "Destek erişimi bulunamadı.");
        if (!grant.IsActiveAt(now))
        {
            throw new UserAdministrationException(UserAdministrationError.Conflict, "Destek erişimi zaten kapalı ya da süresi dolmuş.");
        }

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == grant.TenantId, cancellationToken);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == grant.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? string.Empty;
        grant.End(now, actingUserId, reason);

        await using (var scope = tenantScopes.Create(new TenantContext(grant.TenantId, actingUserId, Guid.Empty)))
        {
            AuditTenant(scope.ServiceProvider, tenant, actingUserId,
                before: new JsonObject { ["supportAccess"] = Describe(name, grant) }, after: new JsonObject { ["supportAccess"] = null });
            await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken);
        }

        db.PlatformAuditLog.Add(new PlatformAuditEntry(
            actingUserId, PlatformAuditActions.SupportAccessRevoked, grant.TenantId, grant.EndReason,
            new JsonObject { ["grantId"] = grant.Id }.ToJsonString(), Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SupportAccessInfo>> ListAsync(Guid? tenantId, bool activeOnly, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await (
            from grant in db.SupportAccessGrants.AsNoTracking()
            where (tenantId == null || grant.TenantId == tenantId) && (!activeOnly || (grant.EndedAt == null && grant.ExpiresAt > now))
            join tenant in db.Tenants.AsNoTracking() on grant.TenantId equals tenant.Id
            join user in db.Users.AsNoTracking() on grant.UserId equals user.Id
            orderby grant.GrantedAt descending
            select new { Grant = grant, TenantName = tenant.Name, UserName = user.DisplayName })
            .Take(100)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => ToInfo(r.Grant, r.TenantName, r.UserName, now))];
    }

    // Salt okunur rol: yalnızca açık modüllerin okuma izinleri. Dışa aktarma, kullanıcı, denetim, ayar ve yazma izinleri yoktur;
    // çekirdek (kullanıcı yönetimi, roller, denetim kaydı) kasıtlı olarak dışarıdadır.
    private async Task<Guid> EnsureRoleAsync(IServiceProvider services, Guid tenantId, CancellationToken cancellationToken)
    {
        var enabled = await services.GetRequiredService<IModuleEntitlements>().GetEnabledAsync(tenantId, cancellationToken);
        var permissions = catalog.Modules
            .Where(m => !ModuleCatalog.IsAlwaysEnabled(m.Key) && enabled.Contains(m.Key))
            .SelectMany(m => m.Permissions)
            .Select(p => p.Key)
            .Where(key => key.EndsWith(".read", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        var roles = services.GetRequiredService<IRoleService>();
        var existing = (await roles.ListAsync(cancellationToken)).FirstOrDefault(r => r.Name == RoleName);
        if (existing is not null)
        {
            await roles.UpdateAsync(existing.Id, RoleName, permissions, cancellationToken, scope: RecordScope.All);
            return existing.Id;
        }

        return (await roles.CreateAsync(RoleName, permissions, cancellationToken, scope: RecordScope.All)).Id;
    }

    private static async Task NotifyAdministratorsAsync(
        IServiceProvider services, ModulixDbContext tenantDb, string tenantName, string userName, SupportAccessGrant grant, CancellationToken cancellationToken)
    {
        var addresses = await (
            from assignment in tenantDb.MembershipRoles.AsNoTracking()
            join role in tenantDb.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join membership in tenantDb.Memberships.AsNoTracking() on assignment.MembershipId equals membership.Id
            join user in tenantDb.Users.AsNoTracking() on membership.UserId equals user.Id
            where role.IsSystem && membership.Status == MembershipStatus.Active && user.Email != null
            select new { user.Email, user.DisplayName })
            .Distinct()
            .ToListAsync(cancellationToken);

        var queue = services.GetRequiredService<IEmailQueue>();
        foreach (var admin in addresses)
        {
            var until = grant.ExpiresAt.ToString("dd.MM.yyyy HH:mm 'UTC'");
            queue.Enqueue(new EmailMessage(
                admin.Email!,
                $"{tenantName}: destek erişimi açıldı",
                $"<p>Merhaba {WebUtility.HtmlEncode(admin.DisplayName)},</p>"
                + $"<p>Modulix destek ekibinden <strong>{WebUtility.HtmlEncode(userName)}</strong>, {WebUtility.HtmlEncode(tenantName)} için <strong>salt okunur</strong> destek erişimi açtı. "
                + $"Erişim {until} tarihinde kendiliğinden sona erer ve verilerinizde değişiklik yapılamaz.</p>"
                + $"<p>Gerekçe: {WebUtility.HtmlEncode(grant.Reason)}</p>"
                + "<p>İsterseniz erişimi Ayarlar bölümünden erken kapatabilirsiniz.</p>")
            {
                ToName = admin.DisplayName,
                TextBody = $"{userName}, {tenantName} için salt okunur destek erişimi açtı. Erişim {until} tarihinde sona erer. Gerekçe: {grant.Reason}",
                Reference = $"core.support-access:{grant.Id}",
            });
        }
    }

    private static void AuditTenant(IServiceProvider services, Tenant tenant, Guid actingUserId, JsonObject before, JsonObject after) =>
        services.GetRequiredService<IAuditWriter>().Add(new AuditEntry(
            tenant.Id,
            actingUserId,
            PlatformAdministration.TenantEntityKey,
            tenant.Id,
            AuditAction.Updated,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Describe(string userName, SupportAccessGrant grant) => new()
    {
        ["user"] = userName,
        ["reason"] = grant.Reason,
        ["grantedAt"] = grant.GrantedAt.ToString("O"),
        ["expiresAt"] = grant.ExpiresAt.ToString("O"),
    };

    private static SupportAccessInfo ToInfo(SupportAccessGrant grant, string tenantName, string userName, DateTimeOffset now) => new(
        grant.Id,
        grant.TenantId,
        tenantName,
        grant.UserId,
        userName,
        grant.Reason,
        grant.GrantedAt,
        grant.ExpiresAt,
        grant.EndedAt,
        grant.EndedAt is not null ? SupportAccessState.Revoked : grant.ExpiresAt > now ? SupportAccessState.Active : SupportAccessState.Expired);
}
