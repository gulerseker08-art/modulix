using System.Security.Claims;
using Modulix.Api.Access;
using Modulix.Api.Platform;
using Modulix.Api.Tenancy;
using Modulix.Api.Users;
using Modulix.Core.Modules;
using Modulix.Core.Sectors;
using Modulix.Shared.Sectors;

namespace Modulix.Api.Sectors;

/// <summary>
/// Sektör/faaliyet modeli (ADR-0029, Dilim A). Katalog yönetimi platform yöneticisine açıktır (<c>/api/platform/sectors</c>, tenant
/// gerekmez, firma verisi görülmez). Tenant seçimi firma yöneticisinindir (<c>/api/sectors</c>, <c>core.customization.manage</c>);
/// seçim tenant bağlamında çalışır, platform yöneticisi bu uçlardan başka firmanın seçimine erişemez.
/// </summary>
public static class SectorEndpoints
{
    public static IEndpointRouteBuilder MapSectorEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/platform/sectors")
            .RequireAuthorization()
            .AddEndpointFilter<RequirePlatformAdministratorFilter>()
            .WithTags("Platform");
        catalog.MapGet("/", async (ISectorCatalog service, CancellationToken ct) => ToDto(await service.GetAsync(ct)));
        catalog.MapPost("/nodes", (CreateActivityNodeRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () =>
            {
                var kind = ParseKind(request.Kind);
                var node = await service.CreateNodeAsync(UserId(user), new ActivityNodeInput(request.Code, request.ParentId, kind, request.Name, request.Description, request.SortOrder), ct);
                return Results.Created($"/api/platform/sectors/nodes/{node.Id}", ToDto(node));
            }));
        catalog.MapPut("/nodes/{id:guid}", (Guid id, UpdateActivityNodeRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.UpdateNodeAsync(
                UserId(user), id, new ActivityNodeUpdate(request.Name, request.Description, request.SortOrder, ParseReadiness(request.Readiness), request.Version), ct)))));
        catalog.MapPut("/nodes/{id:guid}/active", (Guid id, SetActiveRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SetNodeActiveAsync(UserId(user), id, request.Active, ct)))));
        catalog.MapPut("/nodes/{id:guid}/capabilities", (Guid id, CodeListRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SetNodeCapabilitiesAsync(UserId(user), id, request.Codes, ct)))));
        catalog.MapPost("/capabilities", (CreateCapabilityRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () =>
            {
                var capability = await service.CreateCapabilityAsync(UserId(user), new CapabilityInput(request.Code, request.Name), ct);
                return Results.Created($"/api/platform/sectors/capabilities/{capability.Code}", ToDto(capability));
            }));
        catalog.MapPut("/capabilities/{code}/dependencies", (string code, CodeListRequest request, ClaimsPrincipal user, ISectorCatalog service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SetCapabilityDependenciesAsync(UserId(user), code, request.Codes, ct)))));

        // Başlangıç paketleri (Dilim D): yalnızca ekler, tekrar çalıştırılabilir; doğrulanmamış paketler listede işaretlidir.
        catalog.MapGet("/starter-packages", (IStarterPackages service) => service.List().Select(p => new StarterPackageDto(p.Code, p.Name, p.Description, p.Verified, p.VerificationNote, p.NodeCount, p.CapabilityCount)).ToList());
        catalog.MapGet("/starter-packages/{code}/preview", (string code, IStarterPackages service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.PreviewAsync(code, ct)))));
        catalog.MapPost("/starter-packages/{code}/install", (string code, ClaimsPrincipal user, IStarterPackages service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.InstallAsync(UserId(user), code, ct)))));

        // Geri alma (Dilim G): paketin düğümlerini pasife alır, silmez; önizleme yazmaz.
        catalog.MapGet("/starter-packages/{code}/retire-preview", (string code, IStarterPackages service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.PreviewRetireAsync(code, ct)))));
        catalog.MapPost("/starter-packages/{code}/retire", (string code, ClaimsPrincipal user, IStarterPackages service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.RetireAsync(UserId(user), code, ct)))));

        // Firma ilişki rolü kataloğu (Dilim E): platform yönetir; rol silinmez, pasife alınır.
        catalog.MapGet("/relationship-roles", async (IRelationshipRoles service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());
        catalog.MapPost("/relationship-roles", (CreateRelationshipRoleRequest request, ClaimsPrincipal user, IRelationshipRoles service, CancellationToken ct) =>
            Run(async () =>
            {
                var role = await service.CreateAsync(UserId(user), request.Code, request.Name, ct);
                return Results.Created($"/api/platform/sectors/relationship-roles/{role.Code}", ToDto(role));
            }));
        catalog.MapPut("/relationship-roles/{code}/active", (string code, SetActiveRequest request, ClaimsPrincipal user, IRelationshipRoles service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SetActiveAsync(UserId(user), code, request.Active, ct)))));

        // Firma formu rolleri seçerken katalogu okur: ürün standardıdır, tenant verisi değildir; her tenant kullanıcısı okuyabilir.
        app.MapGroup("/api/relationship-roles")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Sectors")
            .MapGet("/", async (IRelationshipRoles service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());

        // Sektör kataloğu (ADR-0042): platform yönetir; sektör silinmez, pasife alınır.
        catalog.MapGet("/industries", async (IIndustries service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());
        catalog.MapPost("/industries", (CreateIndustryRequest request, ClaimsPrincipal user, IIndustries service, CancellationToken ct) =>
            Run(async () =>
            {
                var industry = await service.CreateAsync(UserId(user), request.Code, request.Name, ct);
                return Results.Created($"/api/platform/sectors/industries/{industry.Code}", ToDto(industry));
            }));
        catalog.MapPut("/industries/{code}/active", (string code, SetActiveRequest request, ClaimsPrincipal user, IIndustries service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SetActiveAsync(UserId(user), code, request.Active, ct)))));

        // Firma formu sektörü katalogdan seçer: ürün standardıdır, tenant verisi değildir; her tenant kullanıcısı okuyabilir.
        app.MapGroup("/api/industries")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Sectors")
            .MapGet("/", async (IIndustries service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());

        // Müşteri sektörü ve hizmet ihtiyacı seçimi aktif faaliyet ağacını okur; yalnızca ad ve konum döner, yetenek/sürüm bilgisi dönmez.
        app.MapGroup("/api/activity-options")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .WithTags("Sectors")
            .MapGet("/", async (ISectorCatalog service, CancellationToken ct) => ActivityOptions(await service.GetAsync(ct)));

        var selection = app.MapGroup("/api/sectors")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Sectors");
        // Katalog okuması da firma yöneticisine açıktır (seçim ekranı ağacı gösterir); yönetim uçları yalnızca platformdadır.
        selection.MapGet("/catalog", async (ISectorCatalog service, CancellationToken ct) => ToDto(await service.GetAsync(ct)));
        selection.MapGet("/selection", async (ITenantActivitySelection service, CancellationToken ct) => ToDto(await service.GetAsync(ct)));
        selection.MapPost("/selection/preview", async (ActivityIdsRequest request, ITenantActivitySelection service, CancellationToken ct) =>
            ToDto(await service.PreviewAsync(request.NodeIds, ct)));
        selection.MapPut("/selection", (ActivityIdsRequest request, ITenantActivitySelection service, CancellationToken ct) =>
            Run(async () => Results.Ok(ToDto(await service.SaveAsync(request.NodeIds, ct)))));

        return app;
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StarterPackageNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }

        catch (SectorCatalogNotFoundException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: ex.Message);
        }
        catch (SectorCatalogConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ArgumentException ex)
        {
            return UserEndpoints.ToProblem(ex)!;
        }
    }

    private static Guid UserId(ClaimsPrincipal user) => PlatformEndpoints.UserId(user);

    private static ActivityNodeKind ParseKind(string value) =>
        Enum.TryParse<ActivityNodeKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)
            ? kind
            : throw new ArgumentException("Geçersiz düğüm türü (Sector, Group, Activity).", nameof(value));

    private static ModuleReadiness ParseReadiness(string value) =>
        Enum.TryParse<ModuleReadiness>(value, ignoreCase: true, out var readiness) && Enum.IsDefined(readiness)
            ? readiness
            : throw new ArgumentException("Geçersiz hazırlık durumu (CatalogOnly, Available).", nameof(value));

    private static SectorCatalogResponse ToDto(SectorCatalogView view) =>
        new([.. view.Nodes.Select(ToDto)], [.. view.Capabilities.Select(ToDto)]);

    private static ActivityNodeDto ToDto(ActivityNodeView n) =>
        new(n.Id, n.Code, n.ParentId, n.Kind.ToString(), n.Name, n.Description, n.SortOrder, n.IsActive, n.Readiness.ToString(), n.Version, [.. n.Capabilities]);

    private static StarterPackageRetirePlanDto ToDto(StarterPackageRetirePlan p) =>
        new([.. p.NodesToDeactivate], [.. p.NodesAlreadyInactive], [.. p.NodesMissing], [.. p.Skipped], [.. p.Warnings], p.NothingToDo);

    private static StarterPackagePlanDto ToDto(StarterPackagePlan p) =>
        new([.. p.CapabilitiesToCreate], [.. p.CapabilitiesExisting], [.. p.NodesToCreate], [.. p.NodesExisting], [.. p.Warnings], [.. p.Completions], p.NothingToDo);

    private static RelationshipRoleDto ToDto(RelationshipRoleView r) => new(r.Id, r.Code, r.Name, r.IsActive);

    private static IndustryDto ToDto(IndustryView i) => new(i.Id, i.Code, i.Name, i.IsActive);

    private static List<ActivityOptionDto> ActivityOptions(SectorCatalogView view)
    {
        var codes = view.Nodes.ToDictionary(n => n.Id, n => n.Code);
        return [.. view.Nodes.Where(n => n.IsActive)
            .OrderBy(n => n.SortOrder).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new ActivityOptionDto(n.Code, n.Name, n.Kind.ToString(), n.ParentId is { } p ? codes.GetValueOrDefault(p) : null, n.SortOrder))];
    }

    private static CapabilityDto ToDto(CapabilityView c) => new(c.Id, c.Code, c.Name, c.IsActive, [.. c.DependsOn]);

    private static TenantActivitySelectionResponse ToDto(TenantActivitySelectionView v) => new([.. v.SelectedNodeIds], ToDto(v.Preview));

    private static SelectionPreviewDto ToDto(SelectionPreview p) =>
        new([.. p.ContextNodeIds], [.. p.Errors], [.. p.Gaps.Select(g => new CapabilityGapDto(g.Capability, [.. g.RequiredByNodes]))], [.. p.CatalogOnlyNodeIds], p.IsComplete);
}
