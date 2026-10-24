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

    /// <summary>Aktif tenant ister ve modül tenant için kapalıysa 403 döner.</summary>
    public static TBuilder RequireModule<TBuilder>(this TBuilder builder, string moduleKey)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        builder.AddEndpointFilter<TBuilder, RequireTenantFilter>();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<ICurrentAccess>();
            return await access.IsModuleEnabledAsync(moduleKey, context.HttpContext.RequestAborted)
                ? await next(context)
                : Forbidden($"'{moduleKey}' modülü bu tenant için açık değil.");
        });
        return builder;
    }

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
        ModuleCatalog catalog, ICurrentAccess access, IRoleService roles, ITenantContextAccessor tenant, CancellationToken ct)
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
        return Results.Ok(new MyAccessResponse(enabled, permissions, myRoles.Any(r => r.IsSystem)));
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

    /// <summary>Hazır roller (ADR-0005): açık modüllerin şablonları ve "Salt okur".</summary>
    private static async Task<IResult> ListTemplatesAsync(
        ModuleCatalog catalog, IModuleEntitlements entitlements, ITenantContextAccessor tenant, CancellationToken ct)
    {
        var enabled = await entitlements.GetEnabledAsync(tenant.Current!.TenantId, ct);
        return Results.Ok(catalog.TemplatesFor(enabled).Select(t => new RoleTemplateDto(t.Key, t.Name, t.Description, t.Permissions)).ToList());
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
            var role = await roles.CreateAsync(request.Name, request.Permissions ?? [], ct, tenant.Current!.MembershipId);
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
            var role = await roles.UpdateAsync(roleId, request.Name, request.Permissions ?? [], ct, tenant.Current!.MembershipId);
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

    private static async Task<IResult> GetMembershipRolesAsync(Guid membershipId, IRoleService roles, CancellationToken ct)
    {
        var assigned = await roles.GetMembershipRolesAsync(membershipId, ct);
        return assigned is null ? MembershipNotFound() : Results.Ok(assigned.Select(ToDto).ToList());
    }

    /// <summary>Yönetici kuralı kullanıcı yönetimindedir: yönetici rolünü yalnızca yönetici verir veya alır.</summary>
    private static Task<IResult> SetMembershipRolesAsync(
        Guid membershipId,
        SetMembershipRolesRequest request,
        IUserAdministration users,
        CancellationToken ct) =>
        UserEndpoints.Run(() => users.SetRolesAsync(membershipId, request.RoleIds ?? [], ct));

    private static RoleDto ToDto(RoleSummary role) => new(role.Id, role.Name, role.Permissions, role.IsSystem, role.MemberCount);

    private static IResult RoleNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Rol bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult MembershipNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Üyelik bulunamadı.");
}
