using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Shared.Access;

namespace Modulix.Api.Access;

public static class AccessEndpointFilters
{
    /// <summary>
    /// Aktif tenant ve izin ister; yoksa 403. İznin modülü tenant için kapalıysa izin rolde olsa bile 403.
    /// Modül uç noktaları bunu kullanır: <c>.RequirePermission(CrmModule.Permissions.CompaniesRead)</c>.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        builder.AddEndpointFilter<TBuilder, RequireTenantFilter>();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<ICurrentAccess>();
            return await access.HasPermissionAsync(permission, context.HttpContext.RequestAborted)
                ? await next(context)
                : Forbidden($"Bu işlem için '{permission}' izni gerekir.");
        });
        return builder;
    }

    /// <summary>Aktif tenant ve verilen izinlerden en az biri; yoksa 403.</summary>
    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("En az bir izin gerekli.", nameof(permissions));
        }

        builder.AddEndpointFilter<TBuilder, RequireTenantFilter>();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<ICurrentAccess>();
            var granted = await access.GetPermissionsAsync(context.HttpContext.RequestAborted);
            return permissions.Any(granted.Contains)
                ? await next(context)
                : Forbidden($"Bu işlem için şu izinlerden biri gerekir: {string.Join(", ", permissions)}.");
        });
        return builder;
    }

    /// <summary>
    /// Aktif tenant ister ve modül tenant için kapalıysa 403 döner. Modülün lisans süresi ve ek süresi bittiyse (salt okunur,
    /// ADR-0028) okuma yöntemleri (GET, HEAD, OPTIONS) ve yol adı <c>/preview</c> ile biten önizlemeler çalışır, diğerleri 403
    /// döner; veri silinmez. Önizleme uç noktaları (POST olsa da) hiçbir şey yazmaz; yeni bir salt okunur POST ucu bu adla bitmelidir.
    /// </summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, string moduleKey)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        builder.AddEndpointFilter<TBuilder, RequireTenantFilter>();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var access = services.GetRequiredService<ICurrentAccess>();
            if (!await access.IsModuleEnabledAsync(moduleKey, context.HttpContext.RequestAborted))
            {
                return Forbidden($"'{moduleKey}' modülü bu tenant için açık değil.");
            }

            if (!IsReadOnlySafe(context.HttpContext.Request)
                && !await services.GetRequiredService<IModuleLicense>().IsWritableAsync(
                    services.GetRequiredService<ITenantContextAccessor>().Current!.TenantId, moduleKey, context.HttpContext.RequestAborted))
            {
                return Forbidden($"'{moduleKey}' modülünün lisans süresi doldu; modül salt okunur. Yöneticinizle iletişime geçin.");
            }

            return await next(context);
        });
        return builder;
    }

    private static bool IsReadOnlySafe(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
        || request.Path.Value?.TrimEnd('/').EndsWith("/preview", StringComparison.OrdinalIgnoreCase) == true;

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Yetkiniz yok.", detail: detail);
}

public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/tenant")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Tenant");
        tenant.MapGet("/modules", GetModulesAsync);
        tenant.MapGet("/access", GetMyAccessAsync);

        var roles = app.MapGroup("/api/roles")
            .RequireAuthorization()
            .WithTags("Roles");

        // Kullanıcı yöneticisi de rol listesini görür: kullanıcıya rol atarken seçer.
        roles.MapGet("/", ListRolesAsync)
            .RequireAnyPermission(CoreModule.Permissions.ManageRoles, CoreModule.Permissions.ManageUsers);
        roles.MapGet("/permissions", ListAssignablePermissionsAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapGet("/templates", ListTemplatesAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapGet("/scope-types", ListScopeTypesAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapGet("/{roleId:guid}/members", ListMembersAsync)
            .RequireAnyPermission(CoreModule.Permissions.ManageRoles, CoreModule.Permissions.ManageUsers);
        roles.MapPost("/", CreateRoleAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapPut("/{roleId:guid}", UpdateRoleAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapDelete("/{roleId:guid}", DeleteRoleAsync).RequirePermission(CoreModule.Permissions.ManageRoles);

        // Rol atamak kullanıcı yönetimidir: rolleri tanımlamak (core.roles.manage) ayrı bir yetkidir.
        var memberships = app.MapGroup("/api/memberships/{membershipId:guid}/roles")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("Roles");
        memberships.MapGet("/", GetMembershipRolesAsync);
        memberships.MapPut("/", SetMembershipRolesAsync);

        return app;
    }

    private static async Task<IResult> GetModulesAsync(ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct)
    {
        var modules = new List<ModuleDto>();
        foreach (var module in catalog.Modules)
        {
            modules.Add(new ModuleDto(module.Key, module.DisplayName, await access.IsModuleEnabledAsync(module.Key, ct)));
        }

        return Results.Ok(modules);
    }

    private static async Task<IResult> GetMyAccessAsync(
        ModuleCatalog catalog,
        ICurrentAccess access,
        IRoleService roles,
        ITenantContextAccessor tenant,
        IMembershipDirectory directory,
        ICompanyContextAccessor company,
        IModuleLicense license,
        TimeProvider time,
        CancellationToken ct)
    {
        var enabled = new List<string>();
        foreach (var module in catalog.Modules)
        {
            if (await access.IsModuleEnabledAsync(module.Key, ct))
            {
                enabled.Add(module.Key);
            }
        }

        var permissions = (await access.GetPermissionsAsync(ct)).Order(StringComparer.Ordinal).ToList();
        var myRoles = await roles.GetMembershipRolesAsync(tenant.Current!.MembershipId, ct) ?? [];

        // Erişilebilir işletici şirketler ve aktif olanı (ADR-0008): şirket seçici ve kapsam göstergesi bunu kullanır.
        var companyAccess = await directory.GetAccessibleCompaniesAsync(tenant.Current!.MembershipId, ct);
        var companies = companyAccess.Companies.Select(c => new OperatingCompanyDto(c.Id, c.Name, c.IsDefault)).ToList();

        return Results.Ok(new MyAccessResponse(
            enabled,
            permissions,
            myRoles.Any(r => r.IsSystem),
            (Shared.Access.RecordScope)await access.GetScopeAsync(ct),
            companies,
            company.Current?.ActiveCompanyId,
            await LicenseWarningsAsync(catalog, license, tenant.Current!.TenantId, time.GetUtcNow(), ct)));
    }

    /// <summary>Uyarı penceresi: bitişe bu kadar gün kala yönetici uyarılır; bitmiş haklar (ek süre, salt okunur) her zaman listelenir.</summary>
    internal const int LicenseWarningDays = 30;

    private static async Task<List<ModuleLicenseDto>> LicenseWarningsAsync(
        ModuleCatalog catalog, IModuleLicense license, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<ModuleLicenseDto>();
        var licenses = await license.GetAsync(tenantId, ct);
        foreach (var module in catalog.Modules)
        {
            if (licenses.TryGetValue(module.Key, out var info) && info is { ValidUntil: { } until, GraceEndsAt: { } graceEnds }
                && (info.Phase != EntitlementPhase.Active || until <= now.AddDays(LicenseWarningDays)))
            {
                warnings.Add(new ModuleLicenseDto(module.Key, module.DisplayName, (LicensePhase)info.Phase, until, graceEnds));
            }
        }

        return warnings;
    }

    private static async Task<IResult> ListRolesAsync(IRoleService roles, CancellationToken ct) =>
        Results.Ok((await roles.ListAsync(ct)).Select(ToDto).ToList());

    private static async Task<IResult> ListAssignablePermissionsAsync(ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct) =>
        Results.Ok(await AssignablePermissionsAsync(catalog, access, ct));

    /// <summary>Rollere atanabilecek izinler: yalnızca tenant için açık modüllerinkiler.</summary>
    internal static async Task<List<PermissionDto>> AssignablePermissionsAsync(ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct)
    {
        var permissions = new List<PermissionDto>();
        foreach (var module in catalog.Modules)
        {
            if (await access.IsModuleEnabledAsync(module.Key, ct))
            {
                permissions.AddRange(module.Permissions.Select(p =>
                    new PermissionDto(p.Key, p.DisplayName, module.Key, module.DisplayName, p.ResourceName ?? AccessKeys.ResourceOf(p.Key))));
            }
        }

        return permissions;
    }

    /// <summary>Kapsamı ayarlanabilen kayıt türleri ve platform tavanları (ADR-0030 K9); rol ekranı seçenekleri buna göre kapatır.</summary>
    private static async Task<IResult> ListScopeTypesAsync(IRoleService roles, CancellationToken ct) =>
        Results.Ok((await roles.GetScopeTypesAsync(ct))
            .Select(t => new RoleScopeTypeDto(t.EntityKey, t.ModuleKey, t.Label, (Shared.Access.RecordScope?)t.MaxScope))
            .ToList());

    /// <summary>Hazır roller (ADR-0005): açık modüllerin şablonları ve "Salt okur".</summary>
    private static async Task<IResult> ListTemplatesAsync(
        ModuleCatalog catalog, IModuleEntitlements entitlements, ITenantContextAccessor tenant, CancellationToken ct)
    {
        var enabled = await entitlements.GetEnabledAsync(tenant.Current!.TenantId, ct);
        return Results.Ok(catalog.TemplatesFor(enabled)
            .Select(t => new RoleTemplateDto(t.Key, t.Name, t.Description, t.Permissions, (Shared.Access.RecordScope)t.Scope))
            .ToList());
    }

    /// <summary>"Bu rolde kimler var": rolün üyeleri, ada göre sıralı ve sayfalı.</summary>
    private static async Task<IResult> ListMembersAsync(
        Guid roleId, int? page, IRoleService roles, IUserAdministration users, CancellationToken ct)
    {
        if ((await roles.ListAsync(ct)).All(r => r.Id != roleId))
        {
            return RoleNotFound();
        }

        var result = await users.ListAsync(new UserQuery(null, null, roleId, page ?? 1, 50), ct);
        return Results.Ok(new Shared.Users.UserPageResponse(
            result.Items.Select(UserEndpoints.ToDto).ToList(), result.Page, result.PageSize, result.TotalCount));
    }

    // Rol uç noktaları kullanıcı yönetimiyle aynı hata biçimini kullanır: mesaj başlıktadır, alan hatası alanın altında.
    private static async Task<IResult> CreateRoleAsync(
        SaveRoleRequest request, IRoleService roles, ITenantContextAccessor tenant, CancellationToken ct)
    {
        try
        {
            var role = await roles.CreateAsync(
                request.Name, request.Permissions ?? [], ct, tenant.Current!.MembershipId, ToScope(request.Scope) ?? Core.Contracts.Authorization.RecordScope.All,
                ToLevel(request.AssignLevel), request.HideOutOfScopeShared, ToOverrides(request.ScopeOverrides));
            return Results.Created($"/api/roles/{role.Id}", ToDto(role));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> UpdateRoleAsync(
        Guid roleId, SaveRoleRequest request, IRoleService roles, ITenantContextAccessor tenant, CancellationToken ct)
    {
        try
        {
            var role = await roles.UpdateAsync(
                roleId, request.Name, request.Permissions ?? [], ct, tenant.Current!.MembershipId, ToScope(request.Scope), ToLevel(request.AssignLevel),
                request.HideOutOfScopeShared, ToOverrides(request.ScopeOverrides));
            return role is null ? RoleNotFound() : Results.Ok(ToDto(role));
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Üyesi olan rol için <c>moveTo</c> gerekir: üyeler o role taşınır (409 yoksa).</summary>
    private static async Task<IResult> DeleteRoleAsync(
        Guid roleId, Guid? moveTo, IRoleService roles, ITenantContextAccessor tenant, CancellationToken ct)
    {
        try
        {
            return await roles.DeleteAsync(roleId, moveTo, ct, tenant.Current!.MembershipId) ? Results.NoContent() : RoleNotFound();
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> GetMembershipRolesAsync(Guid membershipId, IRoleService roles, IMembershipScope scope, CancellationToken ct)
    {
        // Kapsam dışı üyelik (başka bayinin kullanıcısı) başka tenant'ınki gibi "bulunamadı" döner (ADR-0026).
        var assigned = await scope.CanManageAsync(membershipId, ct) ? await roles.GetMembershipRolesAsync(membershipId, ct) : null;
        return assigned is null ? MembershipNotFound() : Results.Ok(assigned.Select(ToDto).ToList());
    }

    /// <summary>Yönetici kuralı kullanıcı yönetimindedir: yönetici rolünü yalnızca yönetici verir veya alır.</summary>
    private static Task<IResult> SetMembershipRolesAsync(
        Guid membershipId,
        SetMembershipRolesRequest request,
        IUserAdministration users,
        CancellationToken ct) =>
        UserEndpoints.Run(() => users.SetRolesAsync(membershipId, request.RoleIds ?? [], ct));

    private static RoleDto ToDto(RoleSummary role) =>
        new(role.Id, role.Name, role.Permissions, role.IsSystem, role.MemberCount, (Shared.Access.RecordScope)role.Scope,
            (Shared.Access.RoleAssignLevel)role.AssignLevel, role.UnitCount, role.HideOutOfScopeShared,
            role.ScopeOverrides.ToDictionary(o => o.Key, o => (Shared.Access.RecordScope)o.Value));

    // Tanımsız değer RoleService'te alan hatası olur (scopeOverrides).
    private static Dictionary<string, Core.Contracts.Authorization.RecordScope>? ToOverrides(
        IReadOnlyDictionary<string, Shared.Access.RecordScope>? overrides) =>
        overrides?.ToDictionary(o => o.Key, o => (Core.Contracts.Authorization.RecordScope)o.Value);

    // Tanımsız değer Role.SetAssignLevel'da alan hatası olur.
    private static Core.Contracts.Authorization.RoleAssignLevel? ToLevel(Shared.Access.RoleAssignLevel? level) =>
        level is { } value ? (Core.Contracts.Authorization.RoleAssignLevel)value : null;

    // Tanımsız değer Role.SetScope'ta alan hatası olur.
    private static Core.Contracts.Authorization.RecordScope? ToScope(Shared.Access.RecordScope? scope) =>
        scope is { } value ? (Core.Contracts.Authorization.RecordScope)value : null;

    private static IResult RoleNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Rol bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult MembershipNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Üyelik bulunamadı.");
}
