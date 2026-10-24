using System.Security.Claims;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Platform;
using Modulix.Infrastructure.Platform;
using Modulix.Shared.Access;
using Modulix.Shared.Platform;
using Modulix.Shared.Users;

namespace Modulix.Api.Platform;

/// <summary>
/// Platform yönetimi (Y4): /api/platform. Yalnızca platform yöneticisine açıktır; bayrak her istekte veritabanından
/// okunur. Aktif tenant gerekmez ve platform yöneticisi firma verisini görmez (kurallar <see cref="IPlatformAdministration"/>'da).
/// </summary>
public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");

        platform.MapGet("/modules", ListModules);
        platform.MapGet("/tenants", ListTenantsAsync);
        platform.MapGet("/tenants/{tenantId:guid}", GetTenantAsync);
        platform.MapPost("/tenants", CreateTenantAsync);
        platform.MapPut("/tenants/{tenantId:guid}", (Guid tenantId, RenameTenantRequest request, ClaimsPrincipal user, IPlatformAdministration admin, CancellationToken ct) =>
            Run(() => admin.RenameTenantAsync(tenantId, request.Name, UserId(user), ct)));
        platform.MapPost("/tenants/{tenantId:guid}/close", (Guid tenantId, ClaimsPrincipal user, IPlatformAdministration admin, CancellationToken ct) =>
            Run(() => admin.CloseTenantAsync(tenantId, UserId(user), ct)));
        platform.MapPost("/tenants/{tenantId:guid}/reopen", (Guid tenantId, ClaimsPrincipal user, IPlatformAdministration admin, CancellationToken ct) =>
            Run(() => admin.ReopenTenantAsync(tenantId, UserId(user), ct)));
        platform.MapPost("/tenants/{tenantId:guid}/administrators", InviteAdministratorAsync);
        platform.MapPut("/tenants/{tenantId:guid}/modules/{moduleKey}", (Guid tenantId, string moduleKey, SetTenantModuleRequest request, ClaimsPrincipal user, IPlatformAdministration admin, CancellationToken ct) =>
            Run(() => admin.SetModuleAsync(tenantId, moduleKey, request.Enabled, UserId(user), ct)));
        platform.MapGet("/catalog", GetCatalogAsync);

        return app;
    }

    private static PlatformModulesResponse ListModules(ModuleCatalog catalog)
    {
        var modules = catalog.Modules
            .Where(m => !ModuleCatalog.IsAlwaysEnabled(m.Key))
            .Select(m => new PlatformModuleDto(
                m.Key,
                m.DisplayName,
                m.RoleTemplates.Select(t => new RoleTemplateDto(t.Key, t.Name, t.Description, t.Permissions)).ToList()))
            .ToList();

        var readOnly = catalog.TemplatesFor(catalog.Modules.Select(m => m.Key).ToHashSet(StringComparer.Ordinal))
            .Single(t => t.Key == ModuleCatalog.ReadOnlyTemplateKey);
        return new PlatformModulesResponse(modules, new RoleTemplateDto(readOnly.Key, readOnly.Name, readOnly.Description, []));
    }

    /// <summary>Modül kataloğu (Y4b): kayıtlı modüller, izinleri, hazır rolleri ve kaç firmada açık oldukları.</summary>
    private static async Task<List<CatalogModuleDto>> GetCatalogAsync(ModuleCatalog catalog, IPlatformAdministration admin, CancellationToken ct)
    {
        var usage = (await admin.GetModuleUsageAsync(ct)).ToDictionary(u => u.ModuleKey, u => u.EnabledTenantCount);
        return catalog.Modules
            .Select(m => new CatalogModuleDto(
                m.Key,
                m.DisplayName,
                ModuleCatalog.IsAlwaysEnabled(m.Key),
                usage.TryGetValue(m.Key, out var count) ? count : null,
                m.Permissions.Select(p => new PermissionDto(p.Key, p.DisplayName, m.Key, m.DisplayName, p.ResourceName ?? AccessKeys.ResourceOf(p.Key))).ToList(),
                m.RoleTemplates.Select(t => new RoleTemplateDto(t.Key, t.Name, t.Description, t.Permissions)).ToList()))
            .ToList();
    }

    private static async Task<IResult> ListTenantsAsync(
        string? search, TenantState? status, int? page, int? pageSize, IPlatformAdministration admin, CancellationToken ct)
    {
        var result = await admin.ListTenantsAsync(new PlatformTenantQuery(search, (TenantStatus?)status, page ?? 1, pageSize ?? 25), ct);
        return Results.Ok(new PlatformTenantPageResponse(result.Items.Select(ToDto).ToList(), result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> GetTenantAsync(Guid tenantId, IPlatformAdministration admin, CancellationToken ct) =>
        await admin.GetTenantAsync(tenantId, ct) is { } detail ? Results.Ok(ToDto(detail)) : TenantNotFound();

    private static async Task<IResult> CreateTenantAsync(
        CreateTenantRequest request, ClaimsPrincipal user, HttpContext http, IPlatformAdministration admin, CancellationToken ct)
    {
        try
        {
            var result = await admin.CreateTenantAsync(
                new TenantCreation(
                    request.Name,
                    request.Slug,
                    request.Modules ?? [],
                    request.RoleTemplates ?? [],
                    request.AdministratorEmail,
                    request.AdministratorName,
                    PublicUrl.Resolve(http)),
                UserId(user),
                ct);
            return Results.Created($"/api/platform/tenants/{result.Tenant.Tenant.Id}", new CreateTenantResponse(ToDto(result.Tenant), result.ExistingAccount));
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> InviteAdministratorAsync(
        Guid tenantId, InviteTenantAdministratorRequest request, ClaimsPrincipal user, HttpContext http, IPlatformAdministration admin, CancellationToken ct)
    {
        try
        {
            var result = await admin.InviteAdministratorAsync(tenantId, request.Email, request.DisplayName, PublicUrl.Resolve(http), UserId(user), ct);
            var invited = result.User;
            return Results.Ok(new InviteTenantAdministratorResponse(
                new TenantAdministratorDto(invited.MembershipId, invited.DisplayName, invited.Email, (UserStatus)invited.Status),
                result.ExistingAccount));
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static async Task<IResult> Run(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (Exception ex) when (ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    // Kullanıcı yönetimiyle aynı eşleme; "bulunamadı" burada firmadır.
    private static IResult? ToProblem(Exception exception) => exception switch
    {
        UserAdministrationException { Error: UserAdministrationError.NotFound } ex =>
            Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message),
        _ => UserEndpoints.ToProblem(exception),
    };

    private static PlatformTenantDto ToDto(PlatformTenant tenant) => new(
        tenant.Id,
        tenant.Name,
        tenant.Slug,
        (TenantState)tenant.Status,
        tenant.CreatedAt,
        tenant.ActiveMemberCount,
        tenant.AdministratorCount,
        tenant.EnabledModules);

    private static PlatformTenantDetailResponse ToDto(PlatformTenantDetail detail) => new(
        ToDto(detail.Tenant),
        detail.Administrators.Select(a => new TenantAdministratorDto(a.MembershipId, a.DisplayName, a.Email, (UserStatus)a.Status)).ToList());

    private static IResult TenantNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Firma bulunamadı.");

    internal static Guid UserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Oturumda kullanıcı yok."));
}

/// <summary>Platform yöneticisi değilse 403. Bayrak her istekte okunur; kaldırılınca bir sonraki istekte geçerli olur.</summary>
public sealed class RequirePlatformAdministratorFilter(IPlatformAdministration platform) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
            && await platform.IsPlatformAdministratorAsync(userId, http.RequestAborted))
        {
            return await next(context);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Bu alan platform yöneticilerine açıktır.",
            detail: "Bu alan platform yöneticilerine açıktır.");
    }
}

/// <summary>Yeni kapsamda tenant bağlamını kurar (<see cref="HttpTenantContextAccessor"/>).</summary>
public sealed class TenantScopeFactory(IServiceScopeFactory scopes) : ITenantScopeFactory
{
    public AsyncServiceScope Create(TenantContext tenant)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContextAccessor>().Current = tenant;
        return scope;
    }
}
