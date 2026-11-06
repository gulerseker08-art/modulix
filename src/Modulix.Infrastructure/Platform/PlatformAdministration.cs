using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Customization;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Authorization;
using Modulix.Infrastructure.Modules;
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
    ITenantScopeFactory tenantScopes,
    TimeProvider time,
    IOptions<LicenseOptions> license) : IPlatformAdministration
{
    public const int MaxPageSize = 50;

    /// <summary>Denetim kaydındaki kayıt türü.</summary>
    public const string TenantEntityKey = "core.tenant";

    public async Task<PlatformAccess?> GetAccessAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.IsPlatformAdministrator)
            .Select(u => new { u.PlatformRoles, u.TwoFactorEnabled })
            .SingleOrDefaultAsync(cancellationToken);
        return user is null ? null : new PlatformAccess(PlatformRoles.Effective(true, user.PlatformRoles), user.TwoFactorEnabled);
    }

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
        // Başlığa göre sıralama (ADR-0036); anahtarlar PlatformTenantQuery.SortKeys'tedir, eşit kayıtlar ada göre sıralanır.
        var ordered = query.Sort switch
        {
            "-name" => tenants.OrderByDescending(t => t.Name),
            "slug" => tenants.OrderBy(t => t.Slug),
            "-slug" => tenants.OrderByDescending(t => t.Slug),
            "status" => tenants.OrderBy(t => t.Status),
            "-status" => tenants.OrderByDescending(t => t.Status),
            "createdAt" => tenants.OrderBy(t => t.CreatedAt),
            "-createdAt" => tenants.OrderByDescending(t => t.CreatedAt),
            _ => tenants.OrderBy(t => t.Name),
        };
        var rows = await ordered.ThenBy(t => t.Name).ThenBy(t => t.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        // Üye sayısı ve açık modüller platform tablolarındandır: sayfadaki bütün firmalar için birer sorguyla okunur.
        // Yönetici sayısı firma verisidir (RLS), firma başına bir kapsam ister; listede okunmaz (yalnızca firma detayında).
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
                enabledModules: modulesByTenant.GetValueOrDefault(tenant.Id) ?? new HashSet<string>()));
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

    public Task CloseTenantAsync(Guid tenantId, string reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        var trimmed = Required(reason, "reason", "Kapatma için gerekçe zorunludur.");
        return ChangeAsync(tenantId, actingUserId, PlatformAuditActions.TenantClosed, trimmed, tenant =>
        {
            if (tenant.Status == TenantStatus.Closed)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Firma zaten kapalı.");
            }

            tenant.Close();
        }, cancellationToken);
    }

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
            join tenant in db.Tenants.AsNoTracking() on entry.TargetTenantId equals tenant.Id into targets
            from target in targets.DefaultIfEmpty()
            orderby entry.OccurredAt descending, entry.Id descending
            select new PlatformAuditItem(
                entry.Id, entry.OccurredAt, entry.ActorUserId, actor == null ? null : actor.DisplayName, entry.Action, entry.TargetTenantId, target == null ? null : target.Name,
                entry.Reason, entry.Details))
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
        Guid tenantId, string moduleKey, bool enabled, Guid actingUserId, CancellationToken cancellationToken,
        EntitlementTerms? terms = null, string? reason = null)
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
        // Kapatma ve süre uzatma (bitişi ileri alma ya da kaldırma) gerekçe ister (ADR-0028).
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var extending = terms is not null && existing is { IsEnabled: true, ValidUntil: { } currentEnd }
            && (terms.ValidUntil is null || terms.ValidUntil > currentEnd);
        if (trimmedReason is null && (!enabled || extending))
        {
            throw new ArgumentException(enabled ? "Süre uzatmak için gerekçe zorunludur." : "Modülü kapatmak için gerekçe zorunludur.", nameof(reason));
        }

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
        AuditPlatform(tenantDb, actingUserId, PlatformAuditActions.ModuleChanged, tenantId, trimmedReason ?? terms?.Note, platformDetails);
        await entitlements.SetEnabledAsync(tenantId, moduleKey, enabled, cancellationToken, terms);
        await tenantDb.SaveChangesAsync(cancellationToken);
    }

    public async Task<PlatformScopeLimits> GetScopeLimitsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw NotFound();
        }

        // Tür listesi kayıt depolarından ve firmada açık modüllerden gelir; depolar tenant bağlamında çözülür.
        await using var scope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = scope.ServiceProvider;
        var enabled = (await services.GetRequiredService<IModuleEntitlements>().GetEnabledAsync(tenantId, cancellationToken)).ToHashSet();
        var types = services.GetRequiredService<ScopedRecordTypes>().ForModules(enabled);
        return new PlatformScopeLimits(
            await ReadScopeLimitsAsync(tenantId, cancellationToken),
            [.. types.Select(t => new PlatformScopeType(t.EntityKey, t.ModuleKey, t.Label))]);
    }

    public async Task<PlatformScopeLimitsChange> SetScopeLimitsAsync(
        Guid tenantId, IReadOnlyList<PlatformScopeLimit> limits, string? reason, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw NotFound();

        // Tavan platform tablosunda, denetim kaydı ve rol sayımı tenant bağlamındadır (ADR-0020): tek SaveChanges'te yazılır.
        await using var scope = tenantScopes.Create(new TenantContext(tenantId, actingUserId, Guid.Empty));
        var services = scope.ServiceProvider;
        var tenantDb = services.GetRequiredService<ModulixDbContext>();
        var existing = await ValidateScopeLimitsAsync(services, tenantDb, tenantId, limits, cancellationToken);
        var seen = limits.Select(l => l.EntityKey).ToHashSet(StringComparer.Ordinal);

        var beforeList = existing.OrderBy(l => l.EntityKey, StringComparer.Ordinal)
            .Select(l => new PlatformScopeLimit(l.EntityKey, l.MaxScope, l.AppliesToAdministrators)).ToList();
        var afterList = limits.OrderBy(l => l.EntityKey, StringComparer.Ordinal).ToList();
        var affected = await CountAffectedRolesAsync(tenantDb, afterList, cancellationToken);
        if (beforeList.SequenceEqual(afterList))
        {
            return new PlatformScopeLimitsChange(beforeList, affected);
        }

        foreach (var row in existing.Where(l => !seen.Contains(l.EntityKey)))
        {
            tenantDb.TenantScopeLimits.Remove(row);
        }

        foreach (var limit in afterList)
        {
            if (existing.SingleOrDefault(l => l.EntityKey == limit.EntityKey) is { } row)
            {
                row.Set(limit.MaxScope, limit.AppliesToAdministrators);
            }
            else
            {
                tenantDb.TenantScopeLimits.Add(new TenantScopeLimit(tenantId, limit.EntityKey, limit.MaxScope, limit.AppliesToAdministrators));
            }
        }

        var beforeAudit = new JsonObject { ["scopeLimits"] = ScopeLimitsJson(beforeList) };
        var afterAudit = new JsonObject { ["scopeLimits"] = ScopeLimitsJson(afterList) };
        Audit(services, tenant, actingUserId, AuditAction.Updated, beforeAudit, afterAudit);
        AuditPlatform(
            tenantDb, actingUserId, PlatformAuditActions.ScopeLimitsChanged, tenantId, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            new JsonObject
            {
                ["before"] = beforeAudit["scopeLimits"]!.DeepClone(),
                ["after"] = afterAudit["scopeLimits"]!.DeepClone(),
                ["affectedRoles"] = affected,
            });
        await tenantDb.SaveChangesAsync(cancellationToken);
        return new PlatformScopeLimitsChange(afterList, affected);
    }

    /// <summary>
    /// Önerilen kümeyi doğrular (geçersiz kapsam, bilinmeyen/kapalı tür, yinelenen tür: <see cref="ArgumentException"/>) ve mevcut satırları döner.
    /// Modülü sonradan kapanan türün mevcut tavanı geçerli kalır: ekran tüm kümeyi yeniden gönderir, tavan sessizce silinmesin.
    /// </summary>
    private static async Task<List<TenantScopeLimit>> ValidateScopeLimitsAsync(
        IServiceProvider services, ModulixDbContext tenantDb, Guid tenantId, IReadOnlyList<PlatformScopeLimit> limits, CancellationToken cancellationToken)
    {
        var enabled = (await services.GetRequiredService<IModuleEntitlements>().GetEnabledAsync(tenantId, cancellationToken)).ToHashSet();
        var allowed = services.GetRequiredService<ScopedRecordTypes>().ForModules(enabled).Select(t => t.EntityKey).ToHashSet(StringComparer.Ordinal);
        var existing = await tenantDb.TenantScopeLimits.Where(l => l.TenantId == tenantId).ToListAsync(cancellationToken);
        allowed.UnionWith(existing.Select(l => l.EntityKey));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var limit in limits)
        {
            if (!Enum.IsDefined(limit.MaxScope))
            {
                throw new ArgumentException("Geçersiz kayıt kapsamı.", nameof(limits));
            }

            if (!allowed.Contains(limit.EntityKey))
            {
                throw new ArgumentException(
                    $"'{limit.EntityKey}' için tavan konamaz: kayıt türü bilinmiyor, kapsamı ayarlanamıyor ya da modülü firmada kapalı (kapanmadan önce konmuş tavan korunur).", nameof(limits));
            }

            if (!seen.Add(limit.EntityKey))
            {
                throw new ArgumentException($"'{limit.EntityKey}' için birden fazla tavan verildi.", nameof(limits));
            }
        }

        return existing;
    }

    public async Task<PlatformScopeLimitsPreview> PreviewScopeLimitsAsync(
        Guid tenantId, IReadOnlyList<PlatformScopeLimit> limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw NotFound();
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenantId, Guid.Empty, Guid.Empty));
        var services = scope.ServiceProvider;
        var tenantDb = services.GetRequiredService<ModulixDbContext>();
        var existing = await ValidateScopeLimitsAsync(services, tenantDb, tenantId, limits, cancellationToken);

        var current = existing.ToDictionary(l => l.EntityKey, l => new PlatformScopeLimit(l.EntityKey, l.MaxScope, l.AppliesToAdministrators), StringComparer.Ordinal);
        var proposed = limits.ToDictionary(l => l.EntityKey, StringComparer.Ordinal);
        var keys = current.Keys.Union(proposed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var roleCounts = await CountAffectedRolesPerTypeAsync(tenantDb, proposed.Values.ToList(), cancellationToken);

        // Üye kapsamı çalışma zamanındaki hesapla çözülür (rol istisnası, ekip/departman rolü, yönetici); destek erişimi etkin üye değildir.
        var permissions = services.GetRequiredService<MembershipPermissions>();
        var memberships = await tenantDb.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.Status == MembershipStatus.Active)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        var rows = keys.ToDictionary(k => k, _ => new int[4], StringComparer.Ordinal);
        var newlyMembers = new HashSet<Guid>();
        var releasedMembers = new HashSet<Guid>();
        var newlyAdmins = new HashSet<Guid>();
        var releasedAdmins = new HashSet<Guid>();
        foreach (var membershipId in memberships)
        {
            // Roller üyelik başına bir kez okunur; tür başına tekrar edilmez (kalabalık firmada önizleme yavaşlamasın).
            var (isAdministrator, scopesByType) = await permissions.GetScopesForTypesAsync(membershipId, keys, cancellationToken);
            foreach (var key in keys)
            {
                var scopes = scopesByType[key];
                var before = IsRestricted(current, key, isAdministrator, scopes);
                var after = IsRestricted(proposed, key, isAdministrator, scopes);
                if (before == after)
                {
                    continue;
                }

                // Sıra: yeni kısılan üye, serbest kalan üye, yeni kısılan yönetici, serbest kalan yönetici.
                var (index, set) = (after, isAdministrator) switch
                {
                    (true, false) => (0, newlyMembers),
                    (false, false) => (1, releasedMembers),
                    (true, true) => (2, newlyAdmins),
                    _ => (3, releasedAdmins),
                };
                rows[key][index]++;
                set.Add(membershipId);
            }
        }

        return new PlatformScopeLimitsPreview(
            [.. keys.Select(k => new PlatformScopeImpact(k, roleCounts.GetValueOrDefault(k), rows[k][0], rows[k][1], rows[k][2], rows[k][3]))],
            await CountAffectedRolesAsync(tenantDb, proposed.Values.ToList(), cancellationToken),
            newlyMembers.Count, releasedMembers.Count, newlyAdmins.Count, releasedAdmins.Count);
    }

    /// <summary>
    /// Kişinin o türdeki kapsamlarından biri tavanı aşıyorsa kısılır (kapsamlar birleşir; tavan her birini indirir). Yönetici yalnızca
    /// tavan onu da bağlıyorsa (<c>AppliesToAdministrators</c>); aynı kural <c>MembershipDataScope</c>'ta uygulanır.
    /// </summary>
    private static bool IsRestricted(
        IReadOnlyDictionary<string, PlatformScopeLimit> limits, string entityKey, bool isAdministrator, IReadOnlyList<RecordScope> scopes) =>
        limits.TryGetValue(entityKey, out var limit)
        && (!isAdministrator || limit.AppliesToAdministrators)
        && scopes.Any(s => !RecordScopeRules.Covers(limit.MaxScope, s));

    /// <summary>Tür başına, yönetici olmayan rollerden etkin kapsamı (istisna ya da varsayılan) o türün tavanını aşanların sayısı.</summary>
    private static async Task<Dictionary<string, int>> CountAffectedRolesPerTypeAsync(
        ModulixDbContext tenantDb, IReadOnlyList<PlatformScopeLimit> limits, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (limits.Count == 0)
        {
            return counts;
        }

        var roles = await tenantDb.Roles.AsNoTracking().Where(r => !r.IsSystem).Select(r => new { r.Id, r.Scope }).ToListAsync(cancellationToken);
        var overrides = (await tenantDb.RoleScopeOverrides.AsNoTracking().Select(o => new { o.RoleId, o.EntityKey, o.Scope }).ToListAsync(cancellationToken))
            .ToDictionary(o => (o.RoleId, o.EntityKey), o => o.Scope);
        foreach (var limit in limits)
        {
            counts[limit.EntityKey] = roles.Count(role =>
                !RecordScopeRules.Covers(limit.MaxScope, overrides.TryGetValue((role.Id, limit.EntityKey), out var scope) ? scope : role.Scope));
        }

        return counts;
    }

    private async Task<IReadOnlyList<PlatformScopeLimit>> ReadScopeLimitsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await db.TenantScopeLimits.AsNoTracking()
            .Where(l => l.TenantId == tenantId)
            .Select(l => new { l.EntityKey, l.MaxScope, l.AppliesToAdministrators })
            .ToListAsync(cancellationToken))
        .OrderBy(l => l.EntityKey, StringComparer.Ordinal)
        .Select(l => new PlatformScopeLimit(l.EntityKey, l.MaxScope, l.AppliesToAdministrators))
        .ToList();

    /// <summary>Yönetici olmayan roller arasında, bir türde etkin kapsamı (istisna ya da varsayılan) tavanı aşanların sayısı.</summary>
    private static async Task<int> CountAffectedRolesAsync(
        ModulixDbContext tenantDb, IReadOnlyList<PlatformScopeLimit> limits, CancellationToken cancellationToken)
    {
        if (limits.Count == 0)
        {
            return 0;
        }

        var roles = await tenantDb.Roles.AsNoTracking().Where(r => !r.IsSystem).Select(r => new { r.Id, r.Scope }).ToListAsync(cancellationToken);
        var overrides = (await tenantDb.RoleScopeOverrides.AsNoTracking().Select(o => new { o.RoleId, o.EntityKey, o.Scope }).ToListAsync(cancellationToken))
            .ToDictionary(o => (o.RoleId, o.EntityKey), o => o.Scope);
        return roles.Count(role => limits.Any(limit =>
            !RecordScopeRules.Covers(limit.MaxScope, overrides.TryGetValue((role.Id, limit.EntityKey), out var scope) ? scope : role.Scope)));
    }

    private static JsonObject ScopeLimitsJson(IEnumerable<PlatformScopeLimit> limits)
    {
        var json = new JsonObject();
        foreach (var limit in limits)
        {
            json[limit.EntityKey] = limit.AppliesToAdministrators ? $"{limit.MaxScope} (yönetici dahil)" : limit.MaxScope.ToString();
        }

        return json;
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

    public async Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Tenants.AsNoTracking()
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken);

        var now = time.GetUtcNow();
        var graceDays = license.Value.EffectiveGraceDays;
        var horizon = now.AddDays(90);
        var ends = await (
            from entitlement in db.ModuleEntitlements.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on entitlement.TenantId equals tenant.Id
            where entitlement.IsEnabled && entitlement.ValidUntil <= horizon && TenantStatusRules.Operational.Contains(tenant.Status)
            select entitlement.ValidUntil!.Value)
            .ToListAsync(cancellationToken);
        return new PlatformOverview(
            counts,
            ends.Count(e => e < now),
            ends.Count(e => EntitlementPhases.At(e, now, graceDays) == EntitlementPhase.ReadOnly),
            ends.Count(e => e >= now && e <= now.AddDays(30)),
            ends.Count(e => e >= now && e <= now.AddDays(60)),
            ends.Count(e => e >= now));
    }

    // Erişime açık firmaların bitiş tarihi olan açık hakları; süresiz haklar ve kapalı firmalar dışarıda.
    public async Task<IReadOnlyList<PlatformExpiration>> ListExpirationsAsync(int days, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var graceDays = license.Value.EffectiveGraceDays;
        var horizon = now.AddDays(Math.Clamp(days, 1, 365));
        var rows = await (
            from entitlement in db.ModuleEntitlements.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on entitlement.TenantId equals tenant.Id
            where entitlement.IsEnabled && entitlement.ValidUntil <= horizon && TenantStatusRules.Operational.Contains(tenant.Status)
            orderby entitlement.ValidUntil, tenant.Name
            select new { tenant.Id, tenant.Name, tenant.Slug, tenant.Status, entitlement.ModuleKey, entitlement.Source, ValidUntil = entitlement.ValidUntil!.Value, entitlement.Note })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new PlatformExpiration(
            r.Id, r.Name, r.Slug, r.Status, r.ModuleKey, r.Source, r.ValidUntil, r.Note,
            EntitlementPhases.At(r.ValidUntil, now, graceDays), r.ValidUntil.AddDays(graceDays)))];
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

        return new PlatformTenant(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status,
            tenant.CreatedAt,
            activeMembers.Value,
            administratorCount,
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
