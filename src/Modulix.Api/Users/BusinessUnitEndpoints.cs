using Modulix.Api.Access;
using Modulix.Core.Modules;
using Modulix.Core.Organization;
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

        // Kullanıcı kartındaki "Departman".
        var membership = app.MapGroup("/api/users/{membershipId:guid}/business-unit")
            .RequireAuthorization()
            .RequirePermission(CoreModule.Permissions.ManageUsers)
            .WithTags("BusinessUnits");
        membership.MapGet("/", async (Guid membershipId, IBusinessUnitService service, CancellationToken ct) =>
            await service.GetMembershipBusinessUnitAsync(membershipId, ct) is { } unit
                ? Results.Ok(new MembershipBusinessUnitDto(unit.Id, unit.Name, unit.PathNames, unit.Kind, unit.IsActive))
                : UserNotFound());
        membership.MapPut("/", (Guid membershipId, SetMembershipBusinessUnitRequest request, IBusinessUnitService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetMembershipBusinessUnitAsync(membershipId, request.BusinessUnitId, ct) ? Results.NoContent() : UserNotFound()));

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

    private static IResult UnitNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Departman bulunamadı.");

    // Başka tenant'ın üyeliği de "bulunamadı" döner; varlığı belli edilmez.
    private static IResult UserNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kullanıcı bulunamadı.");
}
