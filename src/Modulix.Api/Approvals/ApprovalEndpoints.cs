using Modulix.Api.Access;
using Modulix.Core.Approvals;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Modules;
using Modulix.Shared.Approvals;

namespace Modulix.Api.Approvals;

/// <summary>
/// Genel onay (ADR-0027 Dilim 4a). Tanımlar <c>core.approvals.manage</c>, talep açma <c>core.approvals.request</c>, karar
/// <c>core.approvals.decide</c> ister; karar için izin yetmez, kişi etkin adımın onaycısı da olmalıdır. Kullanıcı yalnızca kendi
/// taleplerini ve onaycısı olduğu talepleri görür; <c>core.approvals.manage</c> hepsini görür. Başka tenant'ın kaydı "bulunamadı" döner.
/// </summary>
public static class ApprovalEndpoints
{
    private static readonly string[] AnyApprovalPermission =
        [CoreModule.Permissions.RequestApprovals, CoreModule.Permissions.DecideApprovals, CoreModule.Permissions.ManageApprovals];

    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/approvals").RequireAuthorization().WithTags("Approvals");

        group.MapGet("/definitions", async (bool? activeOnly, IApprovalService service, CancellationToken ct) =>
            Results.Ok((await service.ListDefinitionsAsync(activeOnly ?? false, ct)).Select(ToDto)))
            .RequireAnyPermission(AnyApprovalPermission);

        group.MapPost("/definitions", (SaveApprovalDefinitionRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
        {
            ApprovalDefinition definition = await service.CreateDefinitionAsync(ToInput(request), ct);
            return Results.Created($"/api/approvals/definitions/{definition.Id}", ToDto(definition));
        })).RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapPut("/definitions/{id:guid}", (Guid id, SaveApprovalDefinitionRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.UpdateDefinitionAsync(id, ToInput(request), request.ExpectedVersion, ct) is { } definition ? Results.Ok(ToDto(definition)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapPut("/definitions/{id:guid}/active", (Guid id, SetApprovalDefinitionActiveRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.SetDefinitionActiveAsync(id, request.IsActive, ct) is { } definition ? Results.Ok(ToDto(definition)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapGet("/targets", (ModuleCatalog modules) =>
            Results.Ok(modules.ApprovalTargets.Select(t => new ApprovalTargetDto(t.Target.EntityKey, t.Target.DisplayName))))
            .RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapGet("/records/{entityKey}/{recordId:guid}", async (string entityKey, Guid recordId, IApprovalService service, CancellationToken ct) =>
            await service.RecordViewAsync(new RecordRef(entityKey, recordId), ct) is { } view
                ? Results.Ok(new RecordApprovalViewDto(
                    [.. view.Processes.Select(p => new ApproverOptionDto(p.Id, p.Name))],
                    view.Latest is { } l ? new RecordApprovalStatusDto(l.RequestId, l.Process, l.Status, l.SubmittedAt, l.CompletedAt, l.CurrentStep) : null))
                : NotFound())
            .RequireAnyPermission(AnyApprovalPermission);

        group.MapGet("/approver-options", async (IApprovalService service, CancellationToken ct) =>
        {
            ApproverOptions options = await service.ApproverOptionsAsync(ct);
            return Results.Ok(new ApproverOptionsDto(
                [.. options.People.Select(p => new ApproverOptionDto(p.Id, p.Name))],
                [.. options.Roles.Select(r => new ApproverOptionDto(r.Id, r.Name))]));
        }).RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapGet("/overview", async (IApprovalService service, ICurrentAccess access, CancellationToken ct) =>
        {
            ApprovalOverview overview = await service.OverviewAsync(await SeeAllAsync(access, ct), ct);
            return Results.Ok(new ApprovalOverviewDto(
                overview.WaitingForMe, overview.Overdue, overview.MyOpen, overview.Orphaned, overview.ApprovedLast30Days, overview.RejectedLast30Days,
                overview.AverageDecisionHours,
                [.. overview.Trend.Select(d => new ApprovalDayCountDto(d.Day, d.Approved, d.Rejected))],
                [.. overview.OpenByProcess.Select(p => new ApprovalProcessCountDto(p.Name, p.Open))]));
        }).RequireAnyPermission(AnyApprovalPermission);

        group.MapGet("/requests", async (
            string? view, string? status, Guid? definitionId, string? search, string? sort, int? page, int? pageSize,
            IApprovalService service, ICurrentAccess access, CancellationToken ct) =>
        {
            if (!Enum.TryParse(view ?? nameof(ApprovalRequestView.All), ignoreCase: true, out ApprovalRequestView parsedView))
            {
                return Invalid("view", "Görünüm Waiting, Mine ya da All olmalı.");
            }

            ApprovalRequestStatus? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse(status, ignoreCase: true, out ApprovalRequestStatus value))
                {
                    return Invalid("status", "Durum Pending, Approved, Rejected ya da Withdrawn olmalı.");
                }

                parsedStatus = value;
            }

            ApprovalRequestPage result = await service.ListRequestsAsync(
                new ApprovalRequestQuery(parsedView, parsedStatus, definitionId, search, sort, page ?? 1, pageSize ?? 25), await SeeAllAsync(access, ct), ct);
            return Results.Ok(new ApprovalRequestPageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        }).RequireAnyPermission(AnyApprovalPermission);

        group.MapGet("/requests/{id:guid}", async (Guid id, IApprovalService service, ICurrentAccess access, CancellationToken ct) =>
            await service.GetRequestAsync(id, await SeeAllAsync(access, ct), ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound())
            .RequireAnyPermission(AnyApprovalPermission);

        group.MapPost("/requests", (OpenApprovalRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
        {
            ApprovalRequestDetail detail = await service.OpenRequestAsync(
                new ApprovalRequestInput(request.DefinitionId, request.Title ?? string.Empty, request.Description, request.Amount, request.Currency, request.RecordId), ct);
            return Results.Created($"/api/approvals/requests/{detail.Id}", ToDto(detail));
        })).RequirePermission(CoreModule.Permissions.RequestApprovals);

        group.MapPost("/requests/{id:guid}/approve", (Guid id, DecideApprovalRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.DecideAsync(id, approve: true, request.Comment, request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.DecideApprovals);

        group.MapPost("/requests/{id:guid}/reject", (Guid id, DecideApprovalRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.DecideAsync(id, approve: false, request.Comment, request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.DecideApprovals);

        group.MapPost("/requests/{id:guid}/withdraw", (Guid id, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.WithdrawAsync(id, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.RequestApprovals);

        group.MapPost("/requests/{id:guid}/reassign", (Guid id, ReassignApprovalStepRequest request, IApprovalService service, CancellationToken ct) => RunAsync(async () =>
            await service.ReassignAsync(id, request.StepId, request.UserId, request.RoleId, request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ManageApprovals);

        group.MapGet("/delegations", async (IApprovalService service, ICurrentAccess access, CancellationToken ct) =>
            Results.Ok((await service.ListDelegationsAsync(await SeeAllAsync(access, ct), ct)).Select(ToDto)))
            .RequireAnyPermission(AnyApprovalPermission);

        group.MapPost("/delegations", (CreateApprovalDelegationRequest request, IApprovalService service, ICurrentAccess access, CancellationToken ct) => RunAsync(async () =>
        {
            ApprovalDelegationDetail detail = await service.CreateDelegationAsync(
                new ApprovalDelegationInput(request.PrincipalUserId, request.DelegateUserId, request.StartsAt, request.EndsAt, request.Reason), await SeeAllAsync(access, ct), ct);
            return Results.Created($"/api/approvals/delegations/{detail.Id}", ToDto(detail));
        })).RequireAnyPermission(CoreModule.Permissions.DecideApprovals, CoreModule.Permissions.ManageApprovals);

        group.MapPost("/delegations/{id:guid}/revoke", (Guid id, IApprovalService service, ICurrentAccess access, CancellationToken ct) => RunAsync(async () =>
            await service.RevokeDelegationAsync(id, await SeeAllAsync(access, ct), ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequireAnyPermission(CoreModule.Permissions.DecideApprovals, CoreModule.Permissions.ManageApprovals);

        return app;
    }

    private static ApprovalDelegationDto ToDto(ApprovalDelegationDetail d) => new(
        d.Id, d.PrincipalUserId, d.PrincipalName, d.DelegateUserId, d.DelegateName, d.StartsAt, d.EndsAt, d.Reason, d.IsActive, d.RevokedAt);

    private static Task<bool> SeeAllAsync(ICurrentAccess access, CancellationToken ct) =>
        access.HasPermissionAsync(CoreModule.Permissions.ManageApprovals, ct);

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ApprovalConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ApprovalForbiddenException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.Message);
        }
        catch (ApprovalException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: ex.Message);
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kayıt bulunamadı.");

    private static IResult Invalid(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] }, statusCode: StatusCodes.Status400BadRequest);

    private static ApprovalDefinitionInput ToInput(SaveApprovalDefinitionRequest request) => new(
        request.Name, request.Description, request.Currency, TargetEntityKey: request.TargetEntityKey, Steps:
        [.. (request.Steps ?? []).Select(s => new ApprovalStepDefinition(s.Name, s.Order, s.ApproverUserId, s.ApproverRoleId, s.MinAmount, s.DueHours, s.EscalateToUserId, s.EscalateToRoleId))]);

    private static ApprovalDefinitionDto ToDto(ApprovalDefinition definition) => new(
        definition.Id, definition.Name, definition.Description, definition.Currency, definition.IsActive, definition.Revision, definition.Version, TargetEntityKey: definition.TargetEntityKey, Steps:
        [.. definition.Steps.Select(s => new ApprovalStepDefinitionDto(s.Name, s.Order, s.ApproverUserId, s.ApproverRoleId, s.MinAmount, s.DueHours, s.EscalateToUserId, s.EscalateToRoleId))]);

    private static ApprovalRequestDto ToDto(ApprovalRequestDetail detail) => new(
        detail.Id, detail.DefinitionId, detail.DefinitionName, detail.DefinitionRevision, detail.Title, detail.Description, detail.Amount, detail.Currency,
        detail.RequesterId, detail.RequesterName, detail.Status.ToString(), detail.SubmittedAt, detail.CompletedAt, detail.Version,
        [.. detail.Steps.Select(s => new ApprovalStepDto(
            s.Id, s.Order, s.Name, s.ApproverUserId, s.ApproverRoleId, s.ApproverName, s.Status.ToString(), s.ActivatedAt, s.DueAt, s.IsOverdue, s.IsOrphaned,
            s.DecidedByName, s.DecidedAt, s.Comment, s.OnBehalfOfName, s.EscalationName, s.IsEscalated))],
        detail.CanDecide, detail.CanWithdraw, detail.TargetEntityKey, detail.TargetRecordId, detail.TargetLabel, detail.TargetLink);
}
