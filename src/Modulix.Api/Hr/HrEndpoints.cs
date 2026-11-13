using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Hr;
using Modulix.Modules.Hr.Records;
using Modulix.Shared.Hr;
using Modulix.Shared.Records;

namespace Modulix.Api.Hr;

/// <summary>
/// İK iş akışı uçları (ADR-0042): izin talebi (dilim 2), masraf beyanı (dilim 3) ve zimmet (dilim 4, tutanak PDF'i dahil) işe giriş/çıkış (dilim 5) ve duyuru okundu işareti (dilim 6). Talepler, haklar ve tatiller genel kayıt uçlarındadır (<c>/api/records/hr.leave-request</c>,
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

        // Masraf beyanı iş akışı (dilim 3): gönder, onayla, iade et, reddet, ödendi, iptal. Satırlar ve beyan alanları genel kayıt uçlarındadır.
        group.MapGet("/expense-claims/{id:guid}/actions", async (Guid id, IExpenseWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new HrActionDto(a.Action, a.Label, a.Allowed, a.Message, a.RequiresReason, a.Destructive)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPost("/expense-claims/{id:guid}/actions", async (Guid id, RunHrActionRequest request, IExpenseWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.RunAsync(id, request.Action ?? string.Empty, request.Version, request.Reason, ct);
            return result.IsSuccess
                ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        // Zimmet iş akışı (dilim 4): teslim aldım, iade al, kayıp. Demirbaş ve zimmet kayıtları genel kayıt uçlarındadır.
        group.MapGet("/asset-assignments/{id:guid}/actions", async (Guid id, IAssetWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value!.Select(a => new HrActionDto(a.Action, a.Label, a.Allowed, a.Message, a.RequiresReason, a.Destructive)).ToList())
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapPost("/asset-assignments/{id:guid}/actions", async (Guid id, RunHrActionRequest request, IAssetWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.RunAsync(id, request.Action ?? string.Empty, request.Version, request.Reason, ct);
            return result.IsSuccess
                ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        // Zimmet tutanağı (PDF): zimmet, demirbaş ve çalışan kayıt boru hattından okunur; zimmeti göremeyen için 404.
        group.MapGet("/asset-assignments/{id:guid}/document", async (Guid id, IRecordPipeline pipeline, CancellationToken ct) =>
        {
            var assignment = await pipeline.GetAsync(new RecordRef(HrModule.Entities.AssetAssignment, id), ct);
            if (!assignment.IsSuccess)
            {
                return RecordEndpoints.ToProblem(assignment.Error!);
            }

            var values = assignment.Value!.Values;
            var assetId = Guid.Parse(values["assetId"]!.GetValue<string>());
            var employeeId = Guid.Parse(values["employeeId"]!.GetValue<string>());
            var asset = (await pipeline.GetAsync(new RecordRef(HrModule.Entities.Asset, assetId), ct)).Value?.Values;
            var employee = (await pipeline.GetAsync(new RecordRef(HrModule.Entities.Employee, employeeId), ct)).Value?.Values;
            string? Text(System.Text.Json.Nodes.JsonObject? source, string field) => source?[field]?.GetValue<string>();
            string? Day(string? iso) => DateOnly.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture) : null;
            string? Moment(string? iso) => DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var m)
                ? m.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null;

            var status = Text(values, "status") switch { "returned" => "İade alındı", "lost" => "Kayıp", _ => "Zimmette" };
            var model = new AssetAssignmentPdfModel(
                Text(values, "assetTag") ?? string.Empty, Text(values, "assetName") ?? string.Empty, Text(asset, "categoryKey"), Text(asset, "brand"),
                Text(asset, "model"), Text(asset, "serialNumber"), Text(values, "employeeName") ?? string.Empty, Text(employee, "employeeNumber"),
                Text(employee, "jobTitle"), Day(Text(values, "assignedOn")) ?? string.Empty, Day(Text(values, "expectedReturnOn")), Text(values, "conditionOut"),
                Moment(Text(values, "acknowledgedAt")), Day(Text(values, "returnedOn")), Text(values, "conditionIn"), status,
                DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            return Results.File(AssetAssignmentPdf.Render(model), "application/pdf", $"zimmet-{model.AssetTag}.pdf");
        });

        // İşe giriş/çıkış (dilim 5): süreç başlatma (şablondan görevler tek işlemde), görev ve süreç eylemleri.
        group.MapPost("/employee-checklists/start", async (StartChecklistRequest request, IChecklistWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.StartAsync(request.EmployeeId, request.TemplateId, request.StartDate, ct);
            return result.IsSuccess
                ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
                : RecordEndpoints.ToProblem(result.Error!);
        });

        group.MapGet("/employee-checklists/{id:guid}/actions", async (Guid id, IChecklistWorkflow workflow, CancellationToken ct) =>
            Actions(await workflow.ChecklistActionsAsync(id, ct)));

        group.MapPost("/employee-checklists/{id:guid}/actions", async (Guid id, RunHrActionRequest request, IChecklistWorkflow workflow, CancellationToken ct) =>
            Record(await workflow.RunChecklistAsync(id, request.Action ?? string.Empty, request.Version, request.Reason, ct)));

        group.MapGet("/checklist-tasks/{id:guid}/actions", async (Guid id, IChecklistWorkflow workflow, CancellationToken ct) =>
            Actions(await workflow.TaskActionsAsync(id, ct)));

        group.MapPost("/checklist-tasks/{id:guid}/actions", async (Guid id, RunHrActionRequest request, IChecklistWorkflow workflow, CancellationToken ct) =>
            Record(await workflow.RunTaskAsync(id, request.Action ?? string.Empty, request.Version, request.Reason, ct)));

        // Duyurular (dilim 6): okundu işareti. Duyuruyu göremeyen için 404; daha önce okunduysa yeniden yazılmaz.
        group.MapPost("/announcements/{id:guid}/read", async (Guid id, IAnnouncementService announcements, CancellationToken ct) =>
            Record(await announcements.MarkReadAsync(id, ct)));

        return app;
    }

    private static IResult Actions(RecordResult<IReadOnlyList<LeaveActionState>> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value!.Select(a => new HrActionDto(a.Action, a.Label, a.Allowed, a.Message, a.RequiresReason, a.Destructive)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);

    private static IResult Record(RecordResult<RecordData> result) =>
        result.IsSuccess
            ? Results.Ok(new RecordResponse(result.Value!.EntityKey, result.Value.Id, result.Value.Version, result.Value.Values))
            : RecordEndpoints.ToProblem(result.Error!);
}
