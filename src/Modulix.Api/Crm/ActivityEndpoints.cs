using System.Globalization;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Activities;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Aktivite uç noktaları (/api/crm/activities). CRUD diğer CRM kaynaklarıyla aynı kalıptadır; durum, tamamlanma ve sorumlu
/// değişiklikleri ayrı eylem uç noktalarıdır ve her biri kendi iznini ister (ADR-0015). Liste, takvim penceresi
/// (<c>?filter range=...</c>), ilişki zaman akışı (<c>related</c>/<c>timeline</c>) ve toplu işlem aynı kayıt boru hattından geçer.
/// </summary>
internal static class ActivityEndpoints
{
    /// <summary>Toplu işlemde tek istekte en fazla bu kadar kayıt işlenir.</summary>
    private const int MaxBulk = 100;

    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<ActivityLinkDto, SaveActivityLinkRequest>(crm, "activity-links", CrmModule.Entities.ActivityLink,
            CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite);
        CrmEndpoints.MapResource<ActivityParticipantDto, SaveActivityParticipantRequest>(crm, "activity-participants",
            CrmModule.Entities.ActivityParticipant, CrmModule.Permissions.ActivitiesRead, CrmModule.Permissions.ActivitiesWrite);

        var activities = crm.MapGroup("/activities");
        activities.MapGet("/{id:guid}/related", RelatedAsync).RequirePermission(CrmModule.Permissions.ActivitiesRead);
        activities.MapPost("/bulk", BulkAsync).RequirePermission(CrmModule.Permissions.ActivitiesRead);

        activities.MapPost("/{id:guid}/start", StartAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        activities.MapPost("/{id:guid}/block", BlockAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        activities.MapPost("/{id:guid}/unblock", UnblockAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        activities.MapPost("/{id:guid}/complete", CompleteAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        activities.MapPost("/{id:guid}/cancel", CancelAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        activities.MapPost("/{id:guid}/reopen", ReopenAsync).RequirePermission(CrmModule.Permissions.ActivitiesReopen);
        activities.MapPost("/{id:guid}/reschedule", RescheduleAsync).RequirePermission(CrmModule.Permissions.ActivitiesWrite);
        // Kendine almak yazma, başkasına atamak atama izni ister; hangisinin gerektiğini eylem servisi (boru hattı) denetler.
        activities.MapPost("/{id:guid}/reassign", ReassignAsync).RequirePermission(CrmModule.Permissions.ActivitiesRead);
    }

    private static IResult Activity(RecordResult<RecordData> result) =>
        result.IsSuccess ? Results.Ok(CrmEndpoints.ToDto<ActivityDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static async Task<IResult> StartAsync(Guid id, ActivityVersionRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.StartAsync(id, request.Version, ct));

    private static async Task<IResult> BlockAsync(Guid id, ActivityReasonRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.BlockAsync(id, request.Version, request.Reason ?? string.Empty, ct));

    private static async Task<IResult> UnblockAsync(Guid id, ActivityVersionRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.UnblockAsync(id, request.Version, ct));

    private static async Task<IResult> CancelAsync(Guid id, ActivityReasonRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.CancelAsync(id, request.Version, request.Reason ?? string.Empty, ct));

    private static async Task<IResult> ReopenAsync(Guid id, ActivityReasonRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.ReopenAsync(id, request.Version, request.Reason ?? string.Empty, ct));

    private static async Task<IResult> ReassignAsync(Guid id, ReassignActivityRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.ReassignAsync(id, request.Version, request.OwnerId, ct));

    private static async Task<IResult> RescheduleAsync(Guid id, RescheduleActivityRequest request, IActivityWorkflow workflow, CancellationToken ct) =>
        Activity(await workflow.RescheduleAsync(id, request.Version, ToCommand(request), ct));

    private static RescheduleCommand ToCommand(RescheduleActivityRequest request) =>
        new(request.Reason ?? string.Empty, request.DueAt, request.StartAt, request.EndAt, request.IsAllDay, request.StartDate, request.EndDate,
            request.AllowConflict);

    private static async Task<IResult> CompleteAsync(Guid id, CompleteActivityRequest request, IActivityWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.CompleteAsync(id, request.Version, new CompleteActivityCommand(
            request.Outcome, request.Note, request.CompletedAt, request.ActualMinutes,
            request.NextStep is { } step ? new NextStep(step.Subject, step.Type, step.DueAt, step.OwnerId) : null), ct);
        return result.IsSuccess
            ? Results.Ok(new CompleteActivityResultDto(
                CrmEndpoints.ToDto<ActivityDto>(result.Value!.Activity),
                result.Value.NextStep is { } next ? CrmEndpoints.ToDto<ActivityDto>(next) : null))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Bağlı kayıtlar

    /// <summary>
    /// Aktivitenin bağlı kayıtları: doğrudan ilişkiler + genel ilişki satırları. Başlıklar yalnızca çağıranın görebildiği kayıtlar için
    /// gelir (<c>IActivityLinkTarget</c>); görünmeyen kayıt listede yer almaz, yalnızca sayısı bildirilir. Aktivitenin ayrıntısı özel ve
    /// gizliyse hiçbir ilişki dönmez.
    /// </summary>
    private static async Task<IResult> RelatedAsync(
        Guid id, IRecordPipeline pipeline, IEnumerable<IActivityLinkTarget> targets, CancellationToken ct)
    {
        var activity = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Activity, id), ct);
        if (!activity.IsSuccess)
        {
            return RecordEndpoints.ToProblem(activity.Error!);
        }

        if (activity.Value!.Values["masked"]?.GetValue<bool>() == true)
        {
            return Results.Ok(new ActivityRelatedDto([], 0));
        }

        var candidates = new List<(Guid LinkId, long Version, string Key, Guid RecordId, bool Primary)>();
        foreach (var (field, key) in new[]
        {
            ("companyId", CrmModule.Entities.Company), ("contactId", CrmModule.Entities.Contact),
            ("opportunityId", CrmModule.Entities.Opportunity), ("leadId", CrmModule.Entities.Lead),
        })
        {
            if (Guid.TryParse(activity.Value.Values[field]?.GetValue<string>(), out var recordId))
            {
                candidates.Add((Guid.Empty, 0, key, recordId, false));
            }
        }

        var links = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.ActivityLink, 1, RecordQuery.MaxPageSize, null,
            new Dictionary<string, string> { ["activityId"] = id.ToString() }), ct);
        foreach (var link in links.IsSuccess ? links.Value!.Items : [])
        {
            if (Guid.TryParse(link.Values["recordId"]?.GetValue<string>(), out var recordId))
            {
                candidates.Add((link.Id, link.Version, link.Values["entityKey"]!.GetValue<string>(), recordId, link.Values["isPrimary"]?.GetValue<bool>() ?? false));
            }
        }

        var visible = new List<ActivityLinkDto>();
        var hidden = 0;
        foreach (var (linkId, version, key, recordId, primary) in candidates)
        {
            var title = targets.FirstOrDefault(t => t.EntityKey == key) is { } target ? await target.GetTitleAsync(recordId, ct) : null;
            if (title is null)
            {
                hidden++;
            }
            else
            {
                visible.Add(new ActivityLinkDto(linkId, version, id, key, recordId, primary, title));
            }
        }

        return Results.Ok(new ActivityRelatedDto(visible, hidden));
    }

    // ---- Toplu işlem

    /// <summary>
    /// Toplu işlem kayıt başına bağımsızdır: her biri kendi yetki, kapsam ve sürüm denetiminden geçer, biri başarısız olunca
    /// diğerleri etkilenmez. Yanıt başarı sayısını ve başarısız kayıtların nedenini verir; "hepsi ya da hiçbiri" değildir.
    /// </summary>
    private static async Task<IResult> BulkAsync(BulkActivityRequest request, IActivityWorkflow workflow, IRecordPipeline pipeline, CancellationToken ct)
    {
        if (request.Items is null or { Count: 0 } || request.Items.Count > MaxBulk)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek.",
                detail: $"Bir seferde 1 ile {MaxBulk} arasında kayıt işlenebilir.");
        }

        if (request.Action is not ("complete" or "cancel" or "reopen" or "reassign" or "reschedule-by-days"))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek.", detail: "Bilinmeyen toplu işlem.");
        }

        var failures = new List<BulkActivityFailureDto>();
        var succeeded = 0;
        foreach (var item in request.Items.DistinctBy(i => i.Id))
        {
            var error = await RunBulkAsync(request, item, workflow, pipeline, ct);
            if (error is null)
            {
                succeeded++;
            }
            else
            {
                failures.Add(new BulkActivityFailureDto(item.Id, error));
            }
        }

        return Results.Ok(new BulkActivityResultDto(failures, succeeded));
    }

    private static async Task<string?> RunBulkAsync(
        BulkActivityRequest request, BulkActivityItem item, IActivityWorkflow workflow, IRecordPipeline pipeline, CancellationToken ct)
    {
        RecordError? error;
        switch (request.Action)
        {
            case "complete":
                error = (await workflow.CompleteAsync(item.Id, item.Version, new CompleteActivityCommand(), ct)).Error;
                break;
            case "cancel":
                error = (await workflow.CancelAsync(item.Id, item.Version, request.Reason ?? string.Empty, ct)).Error;
                break;
            case "reopen":
                error = (await workflow.ReopenAsync(item.Id, item.Version, request.Reason ?? string.Empty, ct)).Error;
                break;
            case "reassign":
                error = request.OwnerId is { } owner
                    ? (await workflow.ReassignAsync(item.Id, item.Version, owner, ct)).Error
                    : new RecordError(RecordErrorCode.ValidationFailed, "Yeni sorumlu zorunludur.");
                break;
            default:
                error = await ShiftAsync(request, item, workflow, pipeline, ct);
                break;
        }

        return error?.Message;
    }

    private static async Task<RecordError?> ShiftAsync(
        BulkActivityRequest request, BulkActivityItem item, IActivityWorkflow workflow, IRecordPipeline pipeline, CancellationToken ct)
    {
        var days = request.Days ?? 0;
        if (days == 0 || Math.Abs(days) > 366)
        {
            return new RecordError(RecordErrorCode.ValidationFailed, "Kaydırılacak gün sayısı 1-366 arası olmalıdır.");
        }

        var current = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Activity, item.Id), ct);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        static DateTimeOffset? Time(RecordData data, string name) =>
            data.Values[name]?.GetValue<string>() is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : null;
        static DateOnly? Day(RecordData data, string name) =>
            data.Values[name]?.GetValue<string>() is { } text ? DateOnly.Parse(text, CultureInfo.InvariantCulture) : null;

        var data = current.Value!;
        var allDay = data.Values["isAllDay"]?.GetValue<bool>() ?? false;
        // Saatli blok yerel saat korunarak kaydırılmaz (UTC gün sayısı); yerel saat korumalı kaydırma tekrar serileriyle birlikte yapılacak.
        var result = await workflow.RescheduleAsync(item.Id, item.Version, new RescheduleCommand(
            request.Reason ?? "Toplu erteleme",
            Time(data, "dueAt")?.AddDays(days), Time(data, "startAt")?.AddDays(days), Time(data, "endAt")?.AddDays(days),
            allDay, Day(data, "startDate")?.AddDays(days), Day(data, "endDate")?.AddDays(days), AllowConflict: true), ct);
        return result.Error;
    }
}
