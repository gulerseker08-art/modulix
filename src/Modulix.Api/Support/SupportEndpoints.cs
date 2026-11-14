using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Modules.Support;
using Modulix.Modules.Support.Lifecycle;
using Modulix.Shared.Support;

namespace Modulix.Api.Support;

/// <summary>
/// Destek uç noktaları (/api/support/...; ADR-0024). Liste, detay, oluşturma ve düzenleme kayıt boru hattından geçer; talep silinemez (depo reddeder).
/// Durum, sorumlu, kuyruk, öncelik ve çözüm yalnızca eylem uç noktalarıyla (<c>POST /api/support/tickets/{id}/{eylem}</c>) değişir; genel PUT bunları
/// yok sayar. Mesajlar (müşteriye görünen yanıt ve iç not) genel kayıt uçlarında yoktur; yalnızca mesaj uç noktalarından, iç not okuma iznine göre döner.
/// Tenant her istekte oturumdan çözülür; istemciden gelen kayıt kimliği tek başına yetki kanıtı değildir (kapsam boru hattında uygulanır).
/// </summary>
public static class SupportEndpoints
{
    private const string Prefix = "/api/support";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static IEndpointRouteBuilder MapSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix)
            .RequireAuthorization()
            .RequireModule(SupportModule.ModuleKey)
            .WithTags("Destek");

        ResourceEndpoints.MapResource<TicketDto, SaveTicketRequest>(group, Prefix, "tickets", SupportModule.Entities.Ticket,
            SupportModule.Permissions.TicketsRead, SupportModule.Permissions.TicketsWrite, SupportModule.Permissions.TicketsCreate);
        ResourceEndpoints.MapResource<QueueDto, SaveQueueRequest>(group, Prefix, "queues", SupportModule.Entities.Queue,
            SupportModule.Permissions.TicketsRead, SupportModule.Permissions.SettingsManage);
        ResourceEndpoints.MapResource<CategoryDto, SaveCategoryRequest>(group, Prefix, "categories", SupportModule.Entities.Category,
            SupportModule.Permissions.TicketsRead, SupportModule.Permissions.SettingsManage);

        group.MapGet("/summary", async (ITicketSummary service, CancellationToken ct) =>
        {
            var result = await service.GetAsync(ct);
            return result.IsSuccess ? Results.Ok(Map<TicketSummaryDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);

        ResourceEndpoints.MapResource<SlaCalendarDto, SaveSlaCalendarRequest>(group, Prefix, "sla-calendars", SupportModule.Entities.SlaCalendar,
            SupportModule.Permissions.SlaManage, SupportModule.Permissions.SlaManage);
        ResourceEndpoints.MapResource<SlaPolicyDto, SaveSlaPolicyRequest>(group, Prefix, "sla-policies", SupportModule.Entities.SlaPolicy,
            SupportModule.Permissions.SlaManage, SupportModule.Permissions.SlaManage);

        // SLA: önizleme yönetim izniyle; talep SLA'sı talep okuma iznine ve veri kapsamına bağlıdır (kapsam dışı talep 404); yeniden hesaplama ayrı izin ve gerekçe ister.
        group.MapPost("/sla/preview", async (SlaPreviewRequest r, ITicketSla sla, CancellationToken ct) =>
        {
            var result = await sla.PreviewAsync(new SlaPreviewInput(r.TypeKey, r.CategoryId, r.Priority, r.PartyId, r.StartAt, r.ContractId), ct);
            return result.IsSuccess ? Results.Ok(Map<SlaPreviewDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.SlaManage);

        ResourceEndpoints.MapResource<RoutingRuleDto, SaveRoutingRuleRequest>(group, Prefix, "routing-rules", SupportModule.Entities.RoutingRule,
            SupportModule.Permissions.SettingsManage, SupportModule.Permissions.SettingsManage);

        // Öncelik matrisi: okuma talep okuma iziniyle (talep ekranı hesaplanan önceliği gösterir), toplu kayıt yönetim iziniyle. Matris yalnızca sonradan açılan/düzenlenen talepleri etkiler.
        group.MapGet("/priority-matrix", async (IPriorityMatrixService matrix, CancellationToken ct) =>
        {
            var result = await matrix.GetAsync(ct);
            return result.IsSuccess ? Results.Ok(Map<List<PriorityCellDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        group.MapPut("/priority-matrix", async (SavePriorityMatrixRequest r, IPriorityMatrixService matrix, CancellationToken ct) =>
        {
            var result = await matrix.SaveAsync([.. r.Cells.Select(c => new PriorityCell(c.Impact, c.Urgency, c.Priority))], ct);
            return result.IsSuccess ? Results.Ok(Map<List<PriorityCellDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.SettingsManage);

        // Yönlendirme denemesi: hangi kural eşleşir, hangi kuyruk/sorumlu çıkar? Hiçbir şey yazmaz.
        group.MapPost("/routing/preview", async (RoutingPreviewRequest r, ITicketRoutingPreview preview, CancellationToken ct) =>
        {
            var result = await preview.PreviewAsync(new RoutingPreviewInput(r.Channel, r.TypeKey, r.CategoryId, r.Priority, r.PartyId), ct);
            return result.IsSuccess ? Results.Ok(Map<RoutingPreviewDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.SettingsManage);

        var tickets = group.MapGroup("/tickets");

        // Olası mükerrer uyarısı: yalnızca çağıranın görebildiği açık talepler aday olur; uyarıdır, kayıt açılmasını engellemez.
        tickets.MapGet("/duplicates", async (Guid? partyId, string? reporterEmail, string? reporterPhone, string? subject, Guid? categoryId, Guid? excludeId, ITicketDuplicates duplicates, CancellationToken ct) =>
        {
            var result = await duplicates.FindAsync(new DuplicateProbe(partyId, reporterEmail, reporterPhone, subject, categoryId, excludeId), ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketDuplicateDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);

        // Birleştirme: ayrı yetki ve gerekçe; kaynak son duruma geçer, geçmiş ve mesajlar korunur.
        tickets.MapPost("/{id:guid}/merge", async (Guid id, MergeTicketRequest r, ITicketMerge merge, CancellationToken ct) =>
        {
            var result = await merge.MergeAsync(id, r.TargetId, r.Version, r.Reason, ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<TicketDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);

        // Alt işler: engelleyici açık alt iş varken talep çözülemez. Yazma talep düzenleme iznine bağlıdır (serviste, talep bulunduktan SONRA).
        tickets.MapGet("/{id:guid}/tasks", async (Guid id, ITicketTasks tasks, CancellationToken ct) =>
        {
            var result = await tasks.ListAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketTaskDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapPost("/{id:guid}/tasks", async (Guid id, AddTicketTaskRequest r, ITicketTasks tasks, CancellationToken ct) =>
        {
            var result = await tasks.AddAsync(id, r.Title, r.AssigneeId, r.IsBlocking, ct);
            return result.IsSuccess ? Results.Created($"{Prefix}/tickets/{id}/tasks", Map<List<TicketTaskDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapPost("/{id:guid}/tasks/{taskId:guid}/complete", async (Guid id, Guid taskId, ITicketTasks tasks, CancellationToken ct) =>
        {
            var result = await tasks.SetDoneAsync(id, taskId, true, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketTaskDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapPost("/{id:guid}/tasks/{taskId:guid}/reopen", async (Guid id, Guid taskId, ITicketTasks tasks, CancellationToken ct) =>
        {
            var result = await tasks.SetDoneAsync(id, taskId, false, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketTaskDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapGet("/{id:guid}/sla", async (Guid id, ITicketSla sla, CancellationToken ct) =>
        {
            var result = await sla.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<SlaInstanceDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapPost("/{id:guid}/sla/recalculate", async (Guid id, SlaRecalculateRequest r, ITicketSla sla, CancellationToken ct) =>
        {
            var result = await sla.RecalculateAsync(id, r.Version, r.Reason, ct);
            return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<TicketDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapGet("/{id:guid}/history", async (Guid id, ITicketQueries queries, CancellationToken ct) =>
        {
            var result = await queries.HistoryAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketHistoryDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);

        // Mesajlar: iç not yalnızca iç not okuma izni olana döner; ekleme izni serviste, talep bulunduktan SONRA denetlenir (kapsam dışı 404, izinsiz 403).
        tickets.MapGet("/{id:guid}/messages", async (Guid id, ITicketMessages messages, CancellationToken ct) =>
        {
            var result = await messages.ListAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketMessageDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        tickets.MapPost("/{id:guid}/messages", async (Guid id, AddTicketMessageRequest r, ITicketMessages messages, CancellationToken ct) =>
        {
            var result = await messages.AddAsync(id, new AddMessageInput(r.Kind, r.Body), ct);
            return result.IsSuccess
                ? Results.Created($"{Prefix}/tickets/{id}/messages", Map<TicketMessageDto>(result.Value!))
                : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);

        SupportChannelEndpoints.MapInternal(group, tickets);

        // Eylemler: izinler serviste denetlenir; sunucu geçiş tablosunu uygular, desteklenmeyen geçiş 409 alır.
        tickets.MapGet("/{id:guid}/actions", async (Guid id, ITicketWorkflow workflow, CancellationToken ct) =>
        {
            var result = await workflow.ActionsAsync(id, ct);
            return result.IsSuccess ? Results.Ok(Map<List<TicketActionDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(SupportModule.Permissions.TicketsRead);
        foreach (var action in TicketAction.All)
        {
            tickets.MapPost($"/{{id:guid}}/{action}", async (Guid id, TicketActionRequest r, ITicketWorkflow workflow, CancellationToken ct) =>
            {
                var result = await workflow.RunAsync(id, action, new TicketActionInput(
                    r.Version, r.Reason, r.OwnerId, r.QueueId, r.ResolutionCode, r.ResolutionNote, r.RootCause, r.CorrectiveAction, r.Priority), ct);
                return result.IsSuccess ? Results.Ok(ResourceEndpoints.ToDto<TicketDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
            }).RequirePermission(SupportModule.Permissions.TicketsRead);
        }

        return app;
    }

    /// <summary>Alan adları aynı olan alan modeli ve DTO arasında JSON üzerinden eşleme.</summary>
    private static T Map<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

/// <summary>Destek politikası: tenant ayarlarından okunur (modül Core'un ayar servisini görmez; host bu köprüyü sağlar).</summary>
internal sealed class SupportPolicyProvider(IServiceProvider services) : ISupportPolicyProvider
{
    public async Task<TicketPolicySettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<Modulix.Core.Contracts.Settings.ITenantSettingReader>();
        return new TicketPolicySettings(
            int.TryParse(await settings.GetValueAsync(SupportModule.SettingKeys.ReopenWindowDays, cancellationToken), out var days) ? Math.Clamp(days, 1, 90) : 7,
            !string.Equals(await settings.GetValueAsync(SupportModule.SettingKeys.RequireRootCause, cancellationToken), "false", StringComparison.OrdinalIgnoreCase),
            int.TryParse(await settings.GetValueAsync(SupportModule.SettingKeys.AutoCloseDays, cancellationToken), out var autoClose) ? Math.Clamp(autoClose, 0, 999) : 0,
            string.Equals(await settings.GetValueAsync(SupportModule.SettingKeys.SurveyEnabled, cancellationToken), "true", StringComparison.OrdinalIgnoreCase),
            int.TryParse(await settings.GetValueAsync(SupportModule.SettingKeys.SurveyValidDays, cancellationToken), out var surveyDays) ? Math.Clamp(surveyDays, 1, 90) : SurveyRules.DefaultValidDays);
    }
}
