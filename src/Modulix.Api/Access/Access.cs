using Modulix.Api.Tenancy;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
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
        roles.MapPost("/", CreateRoleAsync).RequirePermission(CoreModule.Permissions.ManageRoles);
        roles.MapPut("/{roleId:guid}", UpdateRoleAsync).RequirePermission(CoreModule.Permissions.ManageRoles);

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

    private static async Task<IResult> GetMyAccessAsync(ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct)
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
        return Results.Ok(new MyAccessResponse(enabled, permissions));
    }

    private static async Task<IResult> ListRolesAsync(IRoleService roles, CancellationToken ct) =>
        Results.Ok((await roles.ListAsync(ct)).Select(ToDto).ToList());

    /// <summary>Rollere atanabilecek izinler: yalnızca tenant için açık modüllerinkiler.</summary>
    private static async Task<IResult> ListAssignablePermissionsAsync(ModuleCatalog catalog, ICurrentAccess access, CancellationToken ct)
    {
        var permissions = new List<PermissionDto>();
        foreach (var module in catalog.Modules)
        {
            if (await access.IsModuleEnabledAsync(module.Key, ct))
            {
                permissions.AddRange(module.Permissions.Select(p => new PermissionDto(p.Key, p.DisplayName, module.Key)));
            }
        }

        return Results.Ok(permissions);
    }

    private static async Task<IResult> CreateRoleAsync(SaveRoleRequest request, IRoleService roles, CancellationToken ct)
    {
        try
        {
            var role = await roles.CreateAsync(request.Name, request.Permissions ?? [], ct);
            return Results.Created($"/api/roles/{role.Id}", ToDto(role));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex);
        }
    }

    private static async Task<IResult> UpdateRoleAsync(Guid roleId, SaveRoleRequest request, IRoleService roles, CancellationToken ct)
    {
        try
        {
            var role = await roles.UpdateAsync(roleId, request.Name, request.Permissions ?? [], ct);
            return role is null ? RoleNotFound() : Results.Ok(ToDto(role));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex);
        }
        catch (RoleConflictException ex)
        {
            return Conflict(ex);
        }
    }

    private static async Task<IResult> GetMembershipRolesAsync(Guid membershipId, IRoleService roles, CancellationToken ct)
    {
        var assigned = await roles.GetMembershipRolesAsync(membershipId, ct);
        return assigned is null ? MembershipNotFound() : Results.Ok(assigned.Select(ToDto).ToList());
    }

    private static async Task<IResult> SetMembershipRolesAsync(
        Guid membershipId,
        SetMembershipRolesRequest request,
        IRoleService roles,
        CancellationToken ct)
    {
        try
        {
            return await roles.SetMembershipRolesAsync(membershipId, request.RoleIds ?? [], ct)
                ? Results.NoContent()
                : MembershipNotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex);
        }
        catch (RoleConflictException ex)
        {
            return Conflict(ex);
        }
    }

    private static RoleDto ToDto(RoleSummary role) => new(role.Id, role.Name, role.Permissions, role.IsSystem);

    private static IResult BadRequest(ArgumentException ex) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek.", detail: ex.Message);

    private static IResult Conflict(RoleConflictException ex) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "İşlem yapılamadı.", detail: ex.Message);

    private static IResult RoleNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Rol bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult MembershipNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Üyelik bulunamadı.");
}
