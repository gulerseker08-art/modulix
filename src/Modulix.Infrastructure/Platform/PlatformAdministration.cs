using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Organization;
using Modulix.Infrastructure.Persistence;
using Modulix.Infrastructure.Tenancy;
using Modulix.Infrastructure.Users;

namespace Modulix.Infrastructure.Platform;

/// <summary>
/// Belirli bir tenant'ın bağlamında yeni bir DI kapsamı açar. Oturum değeri (RLS) bağlantı açılırken yazılıp sonra
/// değiştirilemediği için başka bir tenant'ın verisine yazmak ayrı kapsam (ayrı bağlantı) ister. Host uygular.
/// </summary>
public interface ITenantScopeFactory
{
    AsyncServiceScope Create(TenantContext tenant);
}

internal sealed class PlatformAdministration(
    ModulixDbContext db,
    ModuleCatalog catalog,
    SectorProfileCatalog profiles,
    ITenantScopeFactory tenantScopes) : IPlatformAdministration
{
    public const int MaxPageSize = 50;

    /// <summary>Denetim kaydındaki kayıt türü.</summary>
    public const string TenantEntityKey = "core.tenant";

    public Task<bool> IsPlatformAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsPlatformAdministrator, cancellationToken);

    public async Task<PlatformTenantPage> ListTenantsAsync(PlatformTenantQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var tenants = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            tenants = tenants.Where(t => t.Name.Contains(search) || t.Slug.Contains(search));
        }

        if (query.Status is { } status)
        {
            tenants = tenants.Where(t => t.Status == status);
        }

        var total = await tenants.CountAsync(cancellationToken);
        var rows = await tenants.OrderBy(t => t.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        // Üye sayısı ve açık modüller platform tablolarındandır: sayfadaki bütün firmalar için birer sorguyla okunur.
        // Yönetici sayısı firma verisidir (RLS) ve firmanın bağlamında okunur; firma başına bir kapsam gerekir.
        var ids = rows.Select(t => t.Id).ToList();
        var memberCounts = await db.Memberships.AsNoTracking()
            .Where(m => ids.Contains(m.TenantId) && m.Status == MembershipStatus.Active)
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TenantId, g => g.Count, cancellationToken);
        var moduleRows = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => ids.Contains(e.TenantId) && e.IsEnabled)
            .Select(e => new { e.TenantId, e.ModuleKey })
            .ToListAsync(cancellationToken);
        var modulesByTenant = moduleRows.GroupBy(e => e.TenantId).ToDictionary(g => g.Key, g => g.Select(e => e.ModuleKey).ToHashSet());

        var items = new List<PlatformTenant>(rows.Count);
        foreach (var tenant in rows)
        {
            items.Add(await SummarizeAsync(
                tenant,
                cancellationToken,
                activeMembers: memberCounts.GetValueOrDefault(tenant.Id),
                enabledModules: modulesByTenant.GetValueOrDefault(tenant.Id)));
        }

        return new PlatformTenantPage(items, page, pageSize, total);
    }

    public async Task<PlatformTenantDetail?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, Guid.Empty, Guid.Empty));
        var administrators = await ListAdministratorsAsync(scope.ServiceProvider.GetRequiredService<ModulixDbContext>(), cancellationToken);
        var summary = await SummarizeAsync(tenant, cancellationToken, administratorCount: administrators.Count(a => a.Status != MembershipStatus.Disabled));
        var entitlements = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => new { e.ModuleKey, e.IsEnabled, e.Source, e.ValidUntil, e.Note })
            .ToListAsync(cancellationToken);
        var order = catalog.Modules.Select((m, i) => (m.Key, i)).ToDictionary(x => x.Key, x => x.i);
        return new PlatformTenantDetail(
            summary,
            administrators,
            [.. entitlements
                .Where(e => order.ContainsKey(e.ModuleKey))
                .OrderBy(e => order[e.ModuleKey])
                .Select(e => new TenantEntitlement(e.ModuleKey, e.IsEnabled, e.Source, e.ValidUntil, e.Note))]);
    }

    public async Task<TenantCreationResult> CreateTenantAsync(TenantCreation creation, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        var name = Required(creation.Name, "name", "Firma adı zorunludur.");
        var slug = Required(creation.Slug, "slug", "Kısa ad zorunludur.");
        var tenant = new Tenant(name, slug, creation.Trial);
        var modules = ValidateModules(creation.Modules ?? []);
        var templates = ValidateTemplates(creation.RoleTemplates ?? [], modules);
        if (creation.Profile is { } profileKey && profiles.Find(profileKey) is null)
        {
            throw new ArgumentException($"Tanımsız sektör profili: '{profileKey}'.", "profile");
        }

        // Yönetici alanları önceden doğrulanır: hızlı başarısızlıkla hiçbir şey oluşturulmaz, hata iletisi formun alanını gösterir.
        try
        {
            UserAdministration.ValidateEmail(creation.AdministratorEmail);
            UserAdministration.ValidateDisplayName(creation.AdministratorName);
        }
        catch (ArgumentException exception) when (exception.ParamName is "email" or "displayName")
        {
            var field = exception.ParamName == "email" ? "administratorEmail" : "administratorName";
            throw new ArgumentException(exception.Message.Replace($" (Parameter '{exception.ParamName}')", string.Empty), field);
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, cancellationToken))
        {
            throw SlugTaken(tenant.Slug);
        }

        // Tenant kaydı, roller, varsayılan şirket, şablonlar, modül hakları, yönetici daveti ve denetim kaydı tek veritabanında
        // tek işlemde yazılır (ADR-0020 Dilim 2): hata olursa yarım kurulmuş tenant kalmaz.
        await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, actingUserId, Guid.Empty));
        var services = scope.ServiceProvider;
        var tenantDb = services.GetRequiredService<ModulixDbContext>();
        var roles = services.GetRequiredService<IRoleService>();

        await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);

        // Tenant kaydı platform şemasında oluşur; iş verisi değildir.
        tenantDb.Tenants.Add(tenant);
        try
        {
            await tenantDb.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw SlugTaken(tenant.Slug);
        }

        var entitlements = services.GetRequiredService<IModuleEntitlements>();
        foreach (var module in modules)
        {
            await entitlements.SetEnabledAsync(tenant.Id, module, enabled: true, cancellationToken);
        }

        var administrator = await roles.EnsureSystemRoleAsync(cancellationToken);
        await DefaultCompany.EnsureAsync(tenantDb, tenant.Name, cancellationToken);
        await DefaultBusinessUnit.EnsureRootAsync(tenantDb, tenant.Name, cancellationToken);
        foreach (var template in templates)
        {
            await roles.CreateAsync(template.Name, template.Permissions, cancellationToken, scope: template.Scope);
        }

        Audit(services, tenant, actingUserId, AuditAction.Created, before: null, after: new JsonObject
        {
            ["name"] = tenant.Name,
            ["slug"] = tenant.Slug,
            ["status"] = tenant.Status.ToString(),
            ["modules"] = new JsonArray([.. modules.Select(m => JsonValue.Create(m))]),
            ["roles"] = new JsonArray([.. templates.Select(t => JsonValue.Create(t.Name))]),
        });
        AuditPlatform(tenantDb, actingUserId, PlatformAuditActions.TenantCreated, tenant.Id, null, new JsonObject
        {
            ["name"] = tenant.Name,
            ["slug"] = tenant.Slug,
            ["status"] = tenant.Status.ToString(),
            ["modules"] = new JsonArray([.. modules.Select(m => JsonValue.Create(m))]),
        });
        await tenantDb.SaveChangesAsync(cancellationToken);

        // Profil seçilen modüllere eklenir; modül listesi yöneticinin seçimidir (ekran profilin modüllerini önceden seçer).
        if (creation.Profile is { } profile)
        {
            await services.GetRequiredService<ISectorProfileService>().ApplyAsync(profile, enableModules: false, cancellationToken);
        }

        InvitationResult invitation;
        try
        {
            invitation = await services.GetRequiredService<UserAdministration>().InviteAsPlatformAsync(
                new UserInvitation(creation.AdministratorEmail, creation.AdministratorName, [administrator.Id], creation.PublicUrl),
                cancellationToken);
        }
        catch (ArgumentException exception) when (exception.ParamName is "email" or "displayName")
        {
            // Formda alanlar yöneticinin alanlarıdır.
            var field = exception.ParamName == "email" ? "administratorEmail" : "administratorName";
            throw new ArgumentException(exception.Message.Replace($" (Parameter '{exception.ParamName}')", string.Empty), field);
        }

        await transaction.CommitAsync(cancellationToken);

        var detail = await GetTenantAsync(tenant.Id, cancellationToken)
            ?? throw new InvalidOperationException("Açılan firma okunamadı.");
        return new TenantCreationResult(detail, invitation.ExistingAccount);
    }

    public async Task RenameTenantAsync(Guid tenantId, string name, Guid actingUserId, CancellationToken cancellationToken)
    {
        var trimmed = Required(name, "name", "Firma adı zorunludur.");
        await ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantRenamed, null, tenant => tenant.Rename(trimmed), cancellationToken);
    }

    public Task CloseTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken, string? reason = null) =>
        ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantClosed, reason, tenant =>
        {
            if (tenant.Status == TenantStatus.Closed)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Firma zaten kapalı.");
            }

            tenant.Close();
        }, cancellationToken);

    public Task ReopenTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken, string? reason = null) =>
        ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantReopened, reason, tenant =>
        {
            if (tenant.IsActive)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Firma zaten açık.");
            }

            tenant.Reopen();
        }, cancellationToken);

    public Task SuspendTenantAsync(Guid tenantId, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        var trimmed = Required(reason, "reason", "Askıya alma için gerekçe zorunludur.");
        return ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantSuspended, trimmed, tenant =>
        {
            if (!tenant.IsActive)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Yalnızca açık firma askıya alınabilir.");
            }

            tenant.Suspend();
        }, cancellationToken);
    }

    public Task ActivateTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken, string? reason = null) =>
        ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantActivated, reason, tenant =>
        {
            if (tenant.Status != TenantStatus.Trial)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Yalnızca deneme firması etkinleştirilir.");
            }

            tenant.Activate();
        }, cancellationToken);

    public async Task<PlatformAuditPage> ListAuditAsync(PlatformAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var entries = db.PlatformAuditLog.AsNoTracking();
        if (query.TenantId is { } tenantId)
        {
            entries = entries.Where(e => e.TargetTenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = entries.Where(e => e.Action == action);
        }

        var total = await entries.CountAsync(cancellationToken);
        var items = await (
            from entry in entries.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize)
            join user in db.Users.AsNoTracking() on entry.ActorUserId equals user.Id into actors
            from actor in actors.DefaultIfEmpty()
            orderby entry.OccurredAt descending, entry.Id descending
            select new PlatformAuditItem(
                entry.Id, entry.OccurredAt, entry.ActorUserId, actor == null ? null : actor.DisplayName, entry.Action, entry.TargetTenantId, entry.Reason, entry.Details))
            .ToListAsync(cancellationToken);
        return new PlatformAuditPage(items, page, pageSize, total);
    }

    public async Task<InvitationResult> InviteAdministratorAsync(
        Guid tenantId, string email, string displayName, string publicUrl, Guid actingUserId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw NotFound();
        if (!tenant.IsActive)
        {
            throw new UserAdministrationException(
                UserAdministrationError.Conflict, "Kapalı veya askıdaki firmaya davet gönderilemez. Önce firmayı yeniden açın.");
        }

        // Davet, Yönetici rolüyle core.membership kaydı olarak firmanın denetim kaydına yazılır. Platform denetim kaydı davetin
        // kaydedildiği SaveChanges'te birlikte yazılsın diye önceden eklenir; davet başarısız olursa kapsamla birlikte atılır.
        await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, actingUserId, Guid.Empty));
        AuditPlatform(scope.ServiceProvider.GetRequiredService<ModulixDbContext>(), actingUserId,
            PlatformAuditActions.AdministratorInvited, tenant.Id, null, null);
        var administrator = await scope.ServiceProvider.GetRequiredService<IRoleService>().EnsureSystemRoleAsync(cancellationToken);
        return await scope.ServiceProvider.GetRequiredService<UserAdministration>().InviteAsPlatformAsync(
            new UserInvitation(email, displayName, [administrator.Id], publicUrl), cancellationToken);
    }

    public async Task SetModuleAsync(
        Guid tenantId, string moduleKey, bool enabled, Guid actingUserId, CancellationToken cancellationToken, EntitlementTerms? terms = null)
    {
        if (!catalog.ContainsModule(moduleKey) || ModuleCatalog.IsAlwaysEnabled(moduleKey))
        {
            throw new ArgumentException($"Açılıp kapatılamayan modül: '{moduleKey}'.", nameof(moduleKey));
        }

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw NotFound();
        }

        // Modül hakkı platform tablolarında, denetim kaydı tenant bağlamındadır (ADR-0020): ayrı SaveChanges adımlarıyla yazılır.
        await using var scope = tenantScopes.Create(new TenantContext(tenantId, actingUserId, Guid.Empty));
        var services = scope.ServiceProvider;
        var tenantDb = services.GetRequiredService<ModulixDbContext>();
        var entitlements = services.GetRequiredService<IModuleEntitlements>();
        // Hak koşulları (kaynak, bitiş, not) yalnızca açarken anlamlıdır; kapatırken verilirse yok sayılır.
        terms = enabled ? terms : null;
        var before = await entitlements.GetEnabledAsync(tenantId, cancellationToken);
        if (before.Contains(moduleKey) == enabled && terms is null)
        {
            return;
        }

        var existing = await tenantDb.ModuleEntitlements.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenantId && e.ModuleKey == moduleKey, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, cancellationToken);
        var after = enabled ? before.Append(moduleKey) : before.Where(m => m != moduleKey);
        var beforeAudit = new JsonObject { ["modules"] = ModuleList(before) };
        var afterAudit = new JsonObject { ["modules"] = ModuleList(after) };
        if (terms is not null)
        {
            // Koşullar değişince (ör. bitiş tarihi) modül listesi aynı kalsa da fark denetim kaydında görünür.
            beforeAudit["terms"] = existing is { IsEnabled: true } ? TermsJson(existing.Source, existing.ValidUntil, existing.Note) : null;
            afterAudit["terms"] = TermsJson(terms.Source, terms.ValidUntil, terms.Note);
        }

        // Denetim kayıtları hak değişikliğinden önce eklenir: SetEnabledAsync hepsini tek SaveChanges'te yazar. Koşullar geçersizse
        // (Enable doğrular) hata fırlar ve hiçbir şey yazılmaz.
        var platformDetails = new JsonObject
        {
            ["module"] = moduleKey,
            ["enabled"] = enabled,
            ["before"] = beforeAudit.DeepClone(),
            ["after"] = afterAudit.DeepClone(),
        };
        Audit(services, tenant, actingUserId, AuditAction.Updated, beforeAudit, afterAudit);
        AuditPlatform(tenantDb, actingUserId, PlatformAuditActions.ModuleChanged, tenantId, terms?.Note, platformDetails);
        await entitlements.SetEnabledAsync(tenantId, moduleKey, enabled, cancellationToken, terms);
        await tenantDb.SaveChangesAsync(cancellationToken);
    }

    private static JsonObject TermsJson(EntitlementSource source, DateTimeOffset? validUntil, string? note) => new()
    {
        ["source"] = source.ToString(),
        ["validUntil"] = validUntil?.ToString("O"),
        ["note"] = note,
    };

    public Task<ProfilePlan?> PreviewProfileAsync(Guid tenantId, string profileKey, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, Guid.Empty, p => p.PreviewAsync(profileKey, enableModules: true, cancellationToken), cancellationToken);

    public Task<ProfilePlan?> ApplyProfileAsync(Guid tenantId, string profileKey, Guid actingUserId, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, actingUserId, p => p.ApplyAsync(profileKey, enableModules: true, cancellationToken), cancellationToken,
            // Platform denetim kaydı profilin kaydedildiği SaveChanges'te birlikte yazılsın diye önceden eklenir.
            audit: tenantDb => AuditPlatform(tenantDb, actingUserId, PlatformAuditActions.ProfileApplied, tenantId, null,
                new JsonObject { ["profile"] = profileKey }));

    private async Task<T> InTenantAsync<T>(
        Guid tenantId, Guid actingUserId, Func<ISectorProfileService, Task<T>> action, CancellationToken cancellationToken,
        Action<ModulixDbContext>? audit = null)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw NotFound();
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenantId, actingUserId, Guid.Empty));
        audit?.Invoke(scope.ServiceProvider.GetRequiredService<ModulixDbContext>());
        return await action(scope.ServiceProvider.GetRequiredService<ISectorProfileService>());
    }

    public async Task<IReadOnlyList<ModuleUsage>> GetModuleUsageAsync(CancellationToken cancellationToken)
    {
        var counts = await (
            from entitlement in db.ModuleEntitlements.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on entitlement.TenantId equals tenant.Id
            where entitlement.IsEnabled && TenantStatusRules.Operational.Contains(tenant.Status)
            group entitlement by entitlement.ModuleKey into g
            select new { ModuleKey = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ModuleKey, g => g.Count, cancellationToken);

        return [.. catalog.Modules
            .Where(m => !ModuleCatalog.IsAlwaysEnabled(m.Key))
            .Select(m => new ModuleUsage(m.Key, counts.GetValueOrDefault(m.Key)))];
    }

    // Katalog sırasıyla; denetim kaydında karşılaştırılabilir olsun.
    private JsonArray ModuleList(IEnumerable<string> modules) =>
        new([.. catalog.Modules.Select(m => m.Key).Where(modules.Contains).Select(k => JsonValue.Create(k))]);

    private async Task ChangeAsync(
        Guid tenantId, Guid actingUserId, string platformAction, string? reason, Action<Tenant> change, CancellationToken cancellationToken)
    {
        // Tenant kaydı platform tablolarındadır (ADR-0020); değişiklik orada, denetim kaydı tenant bağlamında yazılır.
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw NotFound();

        var before = Snapshot(tenant);
        change(tenant);
        var after = Snapshot(tenant);
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenantId, actingUserId, Guid.Empty));
        Audit(scope.ServiceProvider, tenant, actingUserId, AuditAction.Updated, before, after);
        await scope.ServiceProvider.GetRequiredService<ModulixDbContext>().SaveChangesAsync(cancellationToken); // denetim: tenant DB

        // Tenant kaydı ve platform denetim kaydı platform bağlamında tek SaveChanges'te yazılır (ADR-0028).
        AuditPlatform(db, actingUserId, platformAction, tenant.Id, reason,
            new JsonObject { ["before"] = before.DeepClone(), ["after"] = after.DeepClone() });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AuditPlatform(ModulixDbContext target, Guid actingUserId, string action, Guid? tenantId, string? reason, JsonObject? details) =>
        target.PlatformAuditLog.Add(new PlatformAuditEntry(
            actingUserId, action, tenantId, reason, details?.ToJsonString(), Activity.Current?.TraceId.ToString()));

    private async Task<PlatformTenant> SummarizeAsync(
        Tenant tenant,
        CancellationToken cancellationToken,
        int? administratorCount = null,
        int? activeMembers = null,
        IReadOnlySet<string>? enabledModules = null)
    {
        activeMembers ??= await db.Memberships.CountAsync(
            m => m.TenantId == tenant.Id && m.Status == MembershipStatus.Active, cancellationToken);
        var modules = enabledModules ?? (await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenant.Id && e.IsEnabled)
            .Select(e => e.ModuleKey)
            .ToListAsync(cancellationToken)).ToHashSet();

        if (administratorCount is null)
        {
            // Roller firma verisidir (RLS); sayı firmanın bağlamında okunur.
            await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, Guid.Empty, Guid.Empty));
            var administrators = await ListAdministratorsAsync(scope.ServiceProvider.GetRequiredService<ModulixDbContext>(), cancellationToken);
            administratorCount = administrators.Count(a => a.Status != MembershipStatus.Disabled);
        }

        return new PlatformTenant(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status,
            tenant.CreatedAt,
            activeMembers.Value,
            administratorCount.Value,
            [.. catalog.Modules.Select(m => m.Key).Where(modules.Contains)],
            tenant.ProfileKey,
            tenant.ProfileAppliedAt);
    }

    /// <summary>
    /// Yönetici rolündeki üyeler; <paramref name="tenantDb"/> firmanın bağlamında olmalıdır (sistem rolü atamaları RLS altındadır).
    /// Atama, üyelik ve kullanıcı tek sorguda birleştirilir (ADR-0020).
    /// </summary>
    private static async Task<IReadOnlyList<TenantAdministrator>> ListAdministratorsAsync(ModulixDbContext tenantDb, CancellationToken cancellationToken) =>
        await (
            from assignment in tenantDb.MembershipRoles.AsNoTracking()
            join role in tenantDb.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join membership in tenantDb.Memberships.AsNoTracking() on assignment.MembershipId equals membership.Id
            join user in tenantDb.Users.AsNoTracking() on membership.UserId equals user.Id
            where role.IsSystem
            orderby user.DisplayName
            select new TenantAdministrator(membership.Id, user.DisplayName, user.Email ?? string.Empty, membership.Status))
            .Distinct()
            .ToListAsync(cancellationToken);

    private IReadOnlyList<string> ValidateModules(IReadOnlyCollection<string> modules)
    {
        var unknown = modules.Where(m => !catalog.ContainsModule(m) || ModuleCatalog.IsAlwaysEnabled(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Açılamayan modül: {string.Join(", ", unknown)}.", "modules");
        }

        // Katalog sırasıyla; tekrarlar bir kez sayılır.
        return [.. catalog.Modules.Select(m => m.Key).Where(modules.Contains)];
    }

    private IReadOnlyList<RoleTemplate> ValidateTemplates(IReadOnlyCollection<string> keys, IReadOnlyList<string> modules)
    {
        var available = catalog.TemplatesFor(modules.ToHashSet(StringComparer.Ordinal));
        var unknown = keys.Where(k => available.All(t => t.Key != k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Seçilen modüllerde olmayan hazır rol: {string.Join(", ", unknown)}.", "roleTemplates");
        }

        return [.. available.Where(t => keys.Contains(t.Key))];
    }

    private static void Audit(IServiceProvider services, Tenant tenant, Guid actingUserId, AuditAction action, JsonObject? before, JsonObject? after) =>
        services.GetRequiredService<IAuditWriter>().Add(new AuditEntry(
            tenant.Id,
            actingUserId,
            TenantEntityKey,
            tenant.Id,
            action,
            RecordDiff.Compute(before, after),
            ExecutionSource.Api,
            Activity.Current?.TraceId.ToString()));

    private static JsonObject Snapshot(Tenant tenant) => new()
    {
        ["name"] = tenant.Name,
        ["status"] = tenant.Status.ToString(),
    };

    private static string Required(string? value, string field, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message, field) : value.Trim();

    private static UserAdministrationException SlugTaken(string slug) =>
        new(UserAdministrationError.Conflict, $"'{slug}' kısa adı başka bir firmada kullanılıyor.");

    private static UserAdministrationException NotFound() =>
        new(UserAdministrationError.NotFound, "Firma bulunamadı.");
}
