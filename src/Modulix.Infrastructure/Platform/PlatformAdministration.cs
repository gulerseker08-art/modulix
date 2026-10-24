using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Core.Records;
using Modulix.Infrastructure.Persistence;
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

        var items = new List<PlatformTenant>(rows.Count);
        foreach (var tenant in rows)
        {
            items.Add(await SummarizeAsync(tenant, cancellationToken));
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
        var summary = await SummarizeAsync(tenant, cancellationToken, administrators.Count(a => a.Status != MembershipStatus.Disabled));
        return new PlatformTenantDetail(summary, administrators);
    }

    public async Task<TenantCreationResult> CreateTenantAsync(TenantCreation creation, Guid actingUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(creation);
        var name = Required(creation.Name, "name", "Firma adı zorunludur.");
        var slug = Required(creation.Slug, "slug", "Kısa ad zorunludur.");
        var tenant = new Tenant(name, slug);
        var modules = ValidateModules(creation.Modules ?? []);
        var templates = ValidateTemplates(creation.RoleTemplates ?? [], modules);

        if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, cancellationToken))
        {
            throw SlugTaken(tenant.Slug);
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, actingUserId, Guid.Empty));
        var services = scope.ServiceProvider;
        var tenantDb = services.GetRequiredService<ModulixDbContext>();
        var roles = services.GetRequiredService<IRoleService>();

        // Firma, roller, modüller, davet ve e-posta kuyruğu aynı bağlantıda, tek işlemde yazılır.
        await using var transaction = await tenantDb.Database.BeginTransactionAsync(cancellationToken);
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
        foreach (var template in templates)
        {
            await roles.CreateAsync(template.Name, template.Permissions, cancellationToken);
        }

        Audit(services, tenant, actingUserId, AuditAction.Created, before: null, after: new JsonObject
        {
            ["name"] = tenant.Name,
            ["slug"] = tenant.Slug,
            ["status"] = tenant.Status.ToString(),
            ["modules"] = new JsonArray([.. modules.Select(m => JsonValue.Create(m))]),
            ["roles"] = new JsonArray([.. templates.Select(t => JsonValue.Create(t.Name))]),
        });
        await tenantDb.SaveChangesAsync(cancellationToken);

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
        await ChangeAsync(tenantId, actingUserId, tenant => tenant.Rename(trimmed), cancellationToken);
    }

    public Task CloseTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actingUserId, tenant =>
        {
            if (!tenant.IsActive)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Firma zaten kapalı.");
            }

            tenant.Close();
        }, cancellationToken);

    public Task ReopenTenantAsync(Guid tenantId, Guid actingUserId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actingUserId, tenant =>
        {
            if (tenant.IsActive)
            {
                throw new UserAdministrationException(UserAdministrationError.Conflict, "Firma zaten açık.");
            }

            tenant.Reopen();
        }, cancellationToken);

    public async Task<InvitationResult> InviteAdministratorAsync(
        Guid tenantId, string email, string displayName, string publicUrl, Guid actingUserId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw NotFound();
        if (!tenant.IsActive)
        {
            throw new UserAdministrationException(
                UserAdministrationError.Conflict, "Kapalı firmaya davet gönderilemez. Önce firmayı yeniden açın.");
        }

        // Davet, Yönetici rolüyle core.membership kaydı olarak firmanın denetim kaydına yazılır.
        await using var scope = tenantScopes.Create(new TenantContext(tenant.Id, actingUserId, Guid.Empty));
        var administrator = await scope.ServiceProvider.GetRequiredService<IRoleService>().EnsureSystemRoleAsync(cancellationToken);
        return await scope.ServiceProvider.GetRequiredService<UserAdministration>().InviteAsPlatformAsync(
            new UserInvitation(email, displayName, [administrator.Id], publicUrl), cancellationToken);
    }

    private async Task ChangeAsync(Guid tenantId, Guid actingUserId, Action<Tenant> change, CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw NotFound();
        }

        await using var scope = tenantScopes.Create(new TenantContext(tenantId, actingUserId, Guid.Empty));
        var tenantDb = scope.ServiceProvider.GetRequiredService<ModulixDbContext>();
        var tenant = await tenantDb.Tenants.SingleAsync(t => t.Id == tenantId, cancellationToken);

        var before = Snapshot(tenant);
        change(tenant);
        var after = Snapshot(tenant);
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        Audit(scope.ServiceProvider, tenant, actingUserId, AuditAction.Updated, before, after);
        await tenantDb.SaveChangesAsync(cancellationToken);
    }

    private async Task<PlatformTenant> SummarizeAsync(Tenant tenant, CancellationToken cancellationToken, int? administratorCount = null)
    {
        var activeMembers = await db.Memberships.CountAsync(
            m => m.TenantId == tenant.Id && m.Status == MembershipStatus.Active, cancellationToken);
        var modules = await db.ModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenant.Id && e.IsEnabled)
            .Select(e => e.ModuleKey)
            .ToListAsync(cancellationToken);

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
            activeMembers,
            administratorCount.Value,
            [.. catalog.Modules.Select(m => m.Key).Where(modules.Contains)]);
    }

    /// <summary>Yönetici rolündeki üyeler; <paramref name="tenantDb"/> firmanın bağlamında olmalıdır.</summary>
    private static async Task<IReadOnlyList<TenantAdministrator>> ListAdministratorsAsync(ModulixDbContext tenantDb, CancellationToken cancellationToken) =>
        await (
            from assignment in tenantDb.MembershipRoles.AsNoTracking()
            join role in tenantDb.Roles.AsNoTracking() on assignment.RoleId equals role.Id
            join membership in tenantDb.Memberships.AsNoTracking() on assignment.MembershipId equals membership.Id
            join user in tenantDb.Users.AsNoTracking() on membership.UserId equals user.Id
            where role.IsSystem
            orderby user.DisplayName
            select new TenantAdministrator(membership.Id, user.DisplayName, user.Email ?? string.Empty, membership.Status))
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
