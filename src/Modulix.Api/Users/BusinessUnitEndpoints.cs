using Modulix.Api.Access;
using Modulix.Core.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
using Modulix.Core.Platform;
using Modulix.Shared.Users;

namespace Modulix.Api.Users;

/// <summary>
/// Departman ağacı (ADR-0026). Departman ve kullanıcının departmanı kullanıcı yönetiminin parçasıdır:
/// <c>core.users.manage</c> ister. Tenant oturumdan belirlenir; başka tenant'ın departmanı ve üyeliği "bulunamadı" döner.
/// </summary>
public static class BusinessUnitEndpoints
{
    public static IEndpointRouteBuilder MapBusinessUnitEndpoints(this IEndpointRouteBuilder app)
    {
        var units = app.MapGroup("/api/business-units")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("BusinessUnits");
        units.MapGet("/", async (IBusinessUnitService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToDto).ToList()));
        units.MapGet("/{businessUnitId:guid}", async (Guid businessUnitId, IBusinessUnitService service, CancellationToken ct) =>
            await service.GetAsync(businessUnitId, ct) is { } unit ? Results.Ok(ToDto(unit)) : UnitNotFound());
        units.MapPost("/", (CreateBusinessUnitRequest request, IBusinessUnitService service, CancellationToken ct) => RunAsync(async () =>
            await service.CreateAsync(request.ParentId, request.Name, request.Code, request.Kind, ct) is { } unit
                ? Results.Created($"/api/business-units/{unit.Unit.Id}", ToDto(unit))
                : UnitNotFound()));
        units.MapPut("/{businessUnitId:guid}", (Guid businessUnitId, UpdateBusinessUnitRequest request, IBusinessUnitService service, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateAsync(businessUnitId, request.Name, request.Code, request.Kind, ct) is { } unit ? Results.Ok(ToDto(unit)) : UnitNotFound()));
        units.MapPut("/{businessUnitId:guid}/active", (Guid businessUnitId, SetBusinessUnitActiveRequest request, IBusinessUnitService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetActiveAsync(businessUnitId, request.IsActive, ct) is { } unit ? Results.Ok(ToDto(unit)) : UnitNotFound()));

        // Departmana atanmış roller (ADR-0026 K6): departmandaki her kullanıcı rolün izinlerini ve kapsamını alır.
        units.MapGet("/{businessUnitId:guid}/roles", async (Guid businessUnitId, IRoleService roles, CancellationToken ct) =>
            await roles.GetBusinessUnitRolesAsync(businessUnitId, ct) is { } list ? Results.Ok(list.Select(ToDto).ToList()) : UnitNotFound());
        units.MapPut("/{businessUnitId:guid}/roles", (Guid businessUnitId, SetBusinessUnitRolesRequest request, IRoleService roles, ITenantContextAccessor tenant, CancellationToken ct) => RunAsync(async () =>
            await roles.SetBusinessUnitRolesAsync(
                businessUnitId,
                (request.Roles ?? []).Select(r => new BusinessUnitRoleRequest(r.RoleId, r.InheritToChildren)).ToList(),
                ct,
                tenant.Current!.MembershipId) is { } list
                ? Results.Ok(list.Select(ToDto).ToList())
                : UnitNotFound()));

        // Kullanıcı kartındaki "Departman".
        var membership = app.MapGroup("/api/users/{membershipId:guid}/business-unit")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("BusinessUnits");
        membership.MapGet("/", async (Guid membershipId, IBusinessUnitService service, CancellationToken ct) =>
            await service.GetMembershipBusinessUnitAsync(membershipId, ct) is { } unit
                ? Results.Ok(new MembershipBusinessUnitDto(unit.Id, unit.Name, unit.PathNames, unit.Kind, unit.IsActive))
                : UserNotFound());
        membership.MapGet("/change-preview", (Guid membershipId, Guid businessUnitId, IUserAdministration admin, CancellationToken ct) => RunAsync(async () =>
            Results.Ok(ToDto(await admin.PreviewBusinessUnitChangeAsync(membershipId, businessUnitId, ct)))));

        // Kayıt kararı, gerekçe ve kapsam sınırı IUserAdministration'dadır; başka tenant'ın üyeliği "bulunamadı" döner.
        membership.MapPut("/", (Guid membershipId, SetMembershipBusinessUnitRequest request, IUserAdministration admin, CancellationToken ct) => RunAsync(async () =>
        {
            await admin.ChangeBusinessUnitAsync(
                membershipId,
                new BusinessUnitChange(request.BusinessUnitId, request.Reason, request.MoveRecordsWithUser, request.TransferRecordsToMembershipId),
                ct);
            return Results.NoContent();
        }));

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (UserEndpoints.ToProblem(ex) is { } problem)
        {
            return problem;
        }
    }

    private static BusinessUnitDto ToDto(BusinessUnitSummary unit) => new(
        unit.Id, unit.ParentId, unit.Name, unit.Code, unit.Kind, unit.Depth, unit.IsActive, unit.MemberCount, unit.PathNames);

    private static BusinessUnitDetailDto ToDto(BusinessUnitDetail detail) => new(
        ToDto(detail.Unit),
        detail.Members.Select(m => new BusinessUnitMemberDto(m.MembershipId, m.UserId, m.DisplayName, m.Email)).ToList());

    private static BusinessUnitChangePreviewDto ToDto(BusinessUnitChangePreview p) => new(
        p.MembershipId,
        p.DisplayName,
        (UserStatus)p.Status,
        p.CurrentBusinessUnitId,
        p.CurrentPath,
        p.TargetBusinessUnitId,
        p.TargetPath,
        p.IsChange,
        p.OwnedRecords.Select(o => new OwnedRecordsDto(o.EntityKey, o.Label, o.Count)).ToList(),
        p.OwnedTotal,
        p.OwnedUnknown,
        p.DecisionRequired,
        p.ReasonRequired,
        p.LostRoles,
        p.GainedRoles,
        p.Warnings);

    private static BusinessUnitRoleDto ToDto(BusinessUnitRoleAssignment role) =>
        new(role.RoleId, role.RoleName, (Shared.Access.RecordScope)role.Scope, role.InheritToChildren);

    private static IResult UnitNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Departman bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult UserNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
}
