using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Hr;
using Modulix.Modules.Hr.Records;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;

namespace Modulix.Api.Hr;

/// <summary>
/// İK izin talebi iş akışı uçları (ADR-0042 dilim 2). Talepler, haklar ve tatiller genel kayıt uçlarındadır (<c>/api/records/hr.leave-request</c>,
/// <c>hr.leave-balance</c>, <c>hr.holiday</c>); talebin durumu yalnızca buradaki komutlarla değişir. Yetki ve onaylayan kuralı serviste denetlenir;
/// tenant oturumdan gelir. Önizleme yazmaz (yolu <c>/preview</c> ile biter); salt okunur lisans evresinde eylem POST'u 403'tür.
/// </summary>
public static class HrEndpoints
{
    public static IEndpointRouteBuilder MapHrEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr")
            .RequireAuthorization()
            .RequireModule(HrModule.ModuleKey)
            .WithTags("İnsan Kaynakları");

        group.MapGet("/leave-requests/{id:guid}/actions", async (Guid id, ILeaveWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new HrActionDto(a.Action, a.Label, a.Allowed, a.Message, a.RequiresReason, a.Destructive)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPost("/leave-requests/{id:guid}/actions", async (Guid id, RunHrActionRequest request, ILeaveWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.RunAsync(id, request.Action ?? string.Empty, request.Version, request.Reason, ct);
            return result.IsSuccess
                ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPost("/leave-requests/preview", async (LeavePreviewRequest request, ILeaveWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.PreviewAsync(request.EmployeeId, request.LeaveTypeKey, request.StartDate, request.EndDate, request.HalfDay, ct);
            return result.IsSuccess
                ? Results.Ok(new LeavePreviewDto(result.Value!.Days, result.Value.RemainingDays, result.Value.PendingDays))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        return app;
    }
}
