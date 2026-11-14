using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Reporting;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Fırsat uç noktaları (/api/crm/opportunities). CRUD diğer CRM kaynaklarıyla aynı kalıptadır; durum ve kapanış değişiklikleri
/// ayrı eylem uç noktalarıdır (Kanban da bunları çağırır, kurallar tek yerdedir: <see cref="IOpportunityWorkflow"/>). Eylem izinleri
/// serviste, kayıt bulunduktan SONRA denetlenir: kapsam dışı kayıt 404, izinsiz eylem 403 alır. Rapor ve dışa aktarma kayıt boru
/// hattından geçer; kullanıcının izin ve veri kapsamını aşan hiçbir fırsat dönmez.
/// </summary>
internal static class OpportunityEndpoints
{
    private const int MaxRows = 5000;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<OpportunityDto, SaveOpportunityRequest>(crm, "opportunities", CrmModule.Entities.Opportunity,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite);
        CrmEndpoints.MapResource<OpportunityContactDto, SaveOpportunityContactRequest>(crm, "opportunity-contacts", CrmModule.Entities.OpportunityContact,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite);
        CrmEndpoints.MapResource<OpportunityCompetitorDto, SaveOpportunityCompetitorRequest>(crm, "opportunity-competitors", CrmModule.Entities.OpportunityCompetitor,
            CrmModule.Permissions.OpportunitiesRead, CrmModule.Permissions.OpportunitiesWrite);

        var opportunities = crm.MapGroup("/opportunities");
        opportunities.MapGet("/settings", SettingsAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapGet("/report", ReportAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapGet("/export", ExportAsync).RequirePermission(CrmModule.Permissions.OpportunitiesExport);
        opportunities.MapGet("/{id:guid}/history", HistoryAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);

        // Eylem izinleri serviste denetlenir (bkz. sınıf özeti); uç nokta yalnızca okuma iznini ister ki kayıt yoksa 404 dönsün.
        opportunities.MapPost("/{id:guid}/stage", StageAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/win", WinAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/lose", LoseAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/cancel", CancelAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/hold", HoldAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/resume", ResumeAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
        opportunities.MapPost("/{id:guid}/reopen", ReopenAsync).RequirePermission(CrmModule.Permissions.OpportunitiesRead);
    }

    // ---- Eylemler

    private static async Task<IResult> StageAsync(Guid id, ChangeOpportunityStageRequest request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.ChangeStageAsync(id, request.Version, request.Stage ?? string.Empty, request.Note, ct));

    private static async Task<IResult> WinAsync(Guid id, WinOpportunityRequestDto request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.WinAsync(id, request.Version,
            new WinOpportunityRequest(request.ActualCloseDate, request.WonAmount, request.WonCurrency, request.Reason, request.HandoverNote, request.Stage), ct));

    private static async Task<IResult> LoseAsync(Guid id, LoseOpportunityRequestDto request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.LoseAsync(id, request.Version,
            new LoseOpportunityRequest(request.Reason ?? string.Empty, request.Note, request.WinningCompetitorId, request.RecontactDate, request.CreateFollowUp), ct));

    private static async Task<IResult> CancelAsync(Guid id, CancelOpportunityRequest request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.CancelAsync(id, request.Version, request.Reason ?? string.Empty, ct));

    private static async Task<IResult> HoldAsync(Guid id, HoldOpportunityRequest request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.HoldAsync(id, request.Version, request.Reason ?? string.Empty, request.ReviewDate, ct));

    private static async Task<IResult> ResumeAsync(Guid id, ResumeOpportunityRequest request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.ResumeAsync(id, request.Version, request.Note, ct));

    private static async Task<IResult> ReopenAsync(Guid id, ReopenOpportunityRequestDto request, IOpportunityWorkflow workflow, CancellationToken ct) =>
        Action(await workflow.ReopenAsync(id, request.Version,
            new ReopenOpportunityRequest(request.Reason ?? string.Empty, request.Stage ?? string.Empty, request.ExpectedCloseDate, request.Probability), ct));

    private static IResult Action(RecordResult<OpportunityActionResult> result) =>
        result.IsSuccess
            ? Results.Ok(new OpportunityActionResultDto(
                CrmEndpoints.ToDto<OpportunityDto>(result.Value!.Opportunity), result.Value.OpenActivities, result.Value.FollowUpActivityId, result.Value.FollowUpWarning))
            : RecordEndpoints.ToProblem(result.Error!);

    private static async Task<IResult> HistoryAsync(Guid id, IOpportunityWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.HistoryAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.Select(h => new OpportunityHistoryDto(
                h.Id, h.Kind, h.At, h.ChangedBy, h.Cycle, h.FromStage, h.ToStage, h.FromStatus, h.ToStatus, h.FromCloseDate, h.ToCloseDate, h.Reason, h.Details)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Ayarlar ve rapor

    private static async Task<IResult> SettingsAsync(IOpportunityPolicyProvider policy, CancellationToken ct) =>
        Results.Ok(new OpportunitySettingsDto((await policy.GetAsync(ct)).StaleDays));

    private static async Task<IResult> ReportAsync(
        DateOnly? from, DateOnly? to, Guid? ownerId, IOpportunityReport report, TimeProvider time, ISalesReportingPolicyProvider policy, CancellationToken ct)
    {
        // Varsayılan dönem: içinde bulunulan ayın başından bugüne (firmanın saat diliminde).
        var today = ReportPeriods.Today(time.GetUtcNow(), (await policy.GetAsync(ct)).TimeZone);
        var result = await report.BuildAsync(new OpportunityReportQuery(from ?? new DateOnly(today.Year, today.Month, 1), to ?? today, ownerId), ct);
        return result.IsSuccess
            ? Results.Ok(JsonSerializer.Deserialize<OpportunityReportDto>(JsonSerializer.Serialize(result.Value, Json), Json))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Dışa aktarma

    private static async Task<IResult> ExportAsync(
        HttpRequest http, string? search, IRecordPipeline pipeline, ITenantContextAccessor tenant, IAuditWriter audit, IUnitOfWork unitOfWork,
        CancellationToken ct)
    {
        var filters = RecordEndpoints.FiltersFrom(http) ?? new Dictionary<string, string>();
        var items = new List<RecordData>();
        for (var page = 1; items.Count < MaxRows; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Opportunity, page, RecordQuery.MaxPageSize, search, filters), ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            items.AddRange(result.Value!.Items);
            if (items.Count >= result.Value.TotalCount)
            {
                break;
            }
        }

        // Çıkış denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç).
        if (tenant.Current is { } current)
        {
            audit.Add(new AuditEntry(current.TenantId, current.UserId, CrmModule.Entities.Opportunity, Guid.Empty, AuditAction.Exported,
                new JsonObject { ["count"] = items.Count, ["filters"] = string.Join('&', filters.Select(f => $"{f.Key}={f.Value}")) },
                ExecutionSource.Api, null));
            await unitOfWork.SaveChangesAsync(ct);
        }

        // Tutar sütunları hassas alandır: izni olmayan çağıran için kayıt boru hattı zaten göndermez, hücre boş kalır.
        string[] columns =
        [
            "opportunityNumber", "title", "status", "stage", "priority", "amount", "currency", "probability", "expectedCloseDate", "actualCloseDate",
            "wonAmount", "wonCurrency", "lossReason", "ownerId", "companyId", "nextActionDate", "lastInteractionAt",
        ];
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns));
        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => LeadEndpoints.Csv(item.Values[c]?.ToString()))));
        }

        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "firsatlar.csv");
    }
}

/// <summary>Fırsat politikasını tenant ayarlarından okur (modül ayar altyapısına erişemez).</summary>
internal sealed class OpportunityPolicyProvider(IServiceProvider services) : IOpportunityPolicyProvider
{
    public async Task<OpportunityPolicy> GetAsync(CancellationToken cancellationToken)
    {
        // Ayar servisi kayıt boru hattını kullanır; kayıt depolarının (CrmStoreContext) bağımlılığı olduğu için çözümleme gecikmelidir (döngüyü önler).
        var settings = services.GetRequiredService<ITenantSettings>();
        var stale = int.TryParse(await settings.GetValueAsync(CrmModule.SettingKeys.OpportunityStaleDays, cancellationToken),
            NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : OpportunityPolicy.DefaultStaleDays;
        return new OpportunityPolicy(
            OpportunityRules.ParseRequirements(await settings.GetValueAsync(CrmModule.SettingKeys.OpportunityStageRequirements, cancellationToken)),
            OpportunityRules.ParseProbabilities(await settings.GetValueAsync(CrmModule.SettingKeys.OpportunityStageProbabilities, cancellationToken)),
            stale);
    }
}
