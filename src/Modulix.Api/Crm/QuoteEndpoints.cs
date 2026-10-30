using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Sales;
using Modulix.Core.Settings;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Quotes;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Teklif uç noktaları (/api/crm/quotes, /api/crm/quote-lines; ADR-0019). Başlık ve kalem CRUD'u kayıt boru hattından geçer; durum,
/// onay ve toplam alanları genel uçlardan yazılamaz. Durum geçişleri ayrı eylem uç noktalarıdır ve tek kural kümesini
/// (<see cref="IQuoteWorkflow"/>) kullanır. Eylem izinleri serviste, kayıt bulunduktan SONRA denetlenir: kapsam dışı kayıt 404,
/// izinsiz eylem 403 alır. Eylemler kayıt sürümünü ister (eşzamanlı kabul/iptal/revizyonda yalnızca biri kazanır, diğeri 409 alır).
/// </summary>
internal static class QuoteEndpoints
{
    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<QuoteDto, SaveQuoteRequest>(crm, "quotes", CrmModule.Entities.Quote,
            CrmModule.Permissions.QuotesRead, CrmModule.Permissions.QuotesWrite);
        CrmEndpoints.MapResource<QuoteLineDto, SaveQuoteLineRequest>(crm, "quote-lines", CrmModule.Entities.QuoteLine,
            CrmModule.Permissions.QuotesRead, CrmModule.Permissions.QuotesWrite);

        CrmEndpoints.MapResource<QuoteApprovalRuleDto, SaveQuoteApprovalRuleRequest>(crm, "quote-approval-rules", CrmModule.Entities.QuoteApprovalRule,
            CrmModule.Permissions.QuoteRulesManage, CrmModule.Permissions.QuoteRulesManage);

        var quotes = crm.MapGroup("/quotes");
        quotes.MapGet("/{id:guid}/history", HistoryAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/{id:guid}/actions", ActionsAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/settings", SettingsAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        QuoteSharingEndpoints.MapInternal(quotes);
        quotes.MapGet("/{id:guid}/approval-requirements", ApprovalRequirementsAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/{id:guid}/compare", CompareAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/{id:guid}/handover", HandoverAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        crm.MapPost("/opportunities/{id:guid}/primary-quote", async (Guid id, SetPrimaryQuoteRequest r, IQuoteOpportunitySync sync, CancellationToken ct) =>
        {
            var result = await sync.SetPrimaryAsync(id, r.Version, r.QuoteId, ct);
            return result.IsSuccess ? Results.Ok(CrmEndpoints.ToDto<OpportunityDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        }).RequirePermission(CrmModule.Permissions.OpportunitiesRead);

        quotes.MapGet("/report", ReportAsync).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapGet("/export", ExportAsync).RequirePermission(CrmModule.Permissions.QuotesExport);

        // Eylem izinleri serviste denetlenir; uç nokta yalnızca okuma iznini ister ki kayıt yoksa 404 dönsün.
        quotes.MapPost("/{id:guid}/submit-for-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.SubmitForApprovalAsync(id, r.Version, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/withdraw-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.WithdrawApprovalAsync(id, r.Version, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/approve", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.ApproveAsync(id, r.Version, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/reject-approval", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RejectApprovalAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/request-changes", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RequestChangesAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/present", async (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, QuoteDocumentService documents, ILogger<QuoteDocumentService> log, CancellationToken ct) =>
        {
            var result = await w.PresentAsync(id, r.Version, ct);
            if (result.IsSuccess)
            {
                // Müşteri belgesi sunum anında sabitlenir (o günkü ünvan/adres/fiyat). Üretilemezse sunum geri alınmaz; belge sonradan
                // POST /documents ile ya da ilk bağlantı oluşturulurken üretilir.
                try
                {
                    var document = await documents.EnsureAsync(id, ct);
                    if (!document.IsSuccess)
                    {
                        log.LogWarning("Teklif {QuoteId} sunuldu ancak müşteri belgesi üretilemedi: {Error}", id, document.Error!.Message);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    log.LogError(exception, "Teklif {QuoteId} sunuldu ancak müşteri belgesi üretilirken hata oluştu.", id);
                }
            }

            return Ok(result);
        }).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/record-accepted", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RecordAcceptedAsync(id, r.Version, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/record-rejected", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.RecordRejectedAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);
        quotes.MapPost("/{id:guid}/cancel", (Guid id, QuoteTransitionRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Run(w.CancelAsync(id, r.Version, r.Reason ?? string.Empty, ct))).RequirePermission(CrmModule.Permissions.QuotesRead);

        // Revizyon aynı ailede yeni sürüm; çoğaltma yeni aile. İkisi de yeni teklif oluşturma izni (yazma) ister.
        quotes.MapPost("/{id:guid}/revise", async (Guid id, ReviseQuoteRequest r, IQuoteWorkflow w, CancellationToken ct) =>
            Created(await w.ReviseAsync(id, r.Reason, ct))).RequirePermission(CrmModule.Permissions.QuotesWrite);
        quotes.MapPost("/{id:guid}/duplicate", async (Guid id, IQuoteWorkflow w, CancellationToken ct) =>
            Created(await w.DuplicateAsync(id, ct))).RequirePermission(CrmModule.Permissions.QuotesWrite);
    }

    private static async Task<IResult> Run(Task<RecordResult<RecordData>> action) => Ok(await action);

    private static IResult Ok(RecordResult<RecordData> result) =>
        result.IsSuccess ? Results.Ok(CrmEndpoints.ToDto<QuoteDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static IResult Created(RecordResult<RecordData> result) =>
        result.IsSuccess
            ? Results.Created($"/api/crm/quotes/{result.Value!.Id}", CrmEndpoints.ToDto<QuoteDto>(result.Value))
            : RecordEndpoints.ToProblem(result.Error!);

    private static async Task<IResult> HistoryAsync(Guid id, IQuoteWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.HistoryAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.Select(h => new QuoteHistoryDto(h.Id, h.Kind, h.At, h.ActorId, h.FromStatus, h.ToStatus, h.Reason)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> ActionsAsync(Guid id, IQuoteWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.ActionsAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(result.Value!.Select(a => new QuoteActionStateDto(a.Action, a.Allowed, a.Code, a.Message)).ToList())
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> ApprovalRequirementsAsync(Guid id, IQuoteWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.ApprovalRequirementsAsync(id, ct);
        return result.IsSuccess
            ? Results.Ok(new QuoteApprovalRequirementDto(
                result.Value!.Required, [.. result.Value.Matches.Select(m => new QuoteApprovalMatchDto(m.RuleId, m.RuleVersion, m.Name, m.Reason, m.ApproverId))],
                result.Value.ThresholdReason, result.Value.Restricted, result.Value.CanDecide, result.Value.Note))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> CompareAsync(Guid id, Guid with, IQuoteComparison comparison, CancellationToken ct)
    {
        var result = await comparison.CompareAsync(with, id, ct);
        if (!result.IsSuccess)
        {
            return RecordEndpoints.ToProblem(result.Error!);
        }

        static QuoteFieldChangeDto Map(QuoteFieldChange c) => new(c.Field, c.Label, c.Before, c.After);
        var c = result.Value!;
        return Results.Ok(new QuoteComparisonDto(
            c.LeftRevision, c.RightRevision, c.LeftStatus, c.RightStatus, c.Currency, c.RightCurrency, [.. c.Header.Select(Map)], [.. c.Totals.Select(Map)],
            [.. c.Lines.Select(l => new QuoteLineChangeDto(l.Change, l.StableKey, l.Name, [.. l.Fields.Select(Map)]))]));
    }

    /// <summary>Kabul edilmiş teklifin sonraki modüllere devir görünümü (<see cref="IAcceptedQuoteSource"/>); kabul edilmemişse ya da kapsam dışıysa 404.</summary>
    private static async Task<IResult> HandoverAsync(Guid id, IAcceptedQuoteSource source, CancellationToken ct) =>
        await source.GetAsync(id, ct) is { } accepted
            ? Results.Ok(JsonSerializer.Deserialize<AcceptedQuoteDto>(JsonSerializer.Serialize(accepted, JsonSerializerOptions.Web), JsonSerializerOptions.Web))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kabul edilmiş teklif bulunamadı.");

    private static async Task<IResult> ReportAsync(DateOnly? from, DateOnly? to, Guid? ownerId, IQuoteReport report, TimeProvider time, CancellationToken ct)
    {
        // Varsayılan dönem: içinde bulunulan ayın başından bugüne.
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var result = await report.BuildAsync(new QuoteReportQuery(from ?? new DateOnly(today.Year, today.Month, 1), to ?? today, ownerId), ct);
        return result.IsSuccess
            ? Results.Ok(JsonSerializer.Deserialize<QuoteReportDto>(JsonSerializer.Serialize(result.Value, JsonSerializerOptions.Web), JsonSerializerOptions.Web))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    private const int ExportMaxRows = 5000;

    /// <summary>
    /// CSV dışa aktarma. Kayıt boru hattından okunur (izin ve veri kapsamı liste ekranıyla aynı); kalem maliyeti ve iç not sütunu yoktur.
    /// Çıkış denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç). Hücre başındaki formül karakterleri etkisizleştirilir.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpRequest http, string? search, IRecordPipeline pipeline, ITenantContextAccessor tenant, IAuditWriter audit, IUnitOfWork unitOfWork, CancellationToken ct)
    {
        var filters = RecordEndpoints.FiltersFrom(http) ?? new Dictionary<string, string>();
        var items = new List<RecordData>();
        for (var page = 1; items.Count < ExportMaxRows; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Quote, page, RecordQuery.MaxPageSize, search, filters), ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            items.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || items.Count >= result.Value.TotalCount)
            {
                break;
            }
        }

        if (tenant.Current is { } current)
        {
            audit.Add(new AuditEntry(current.TenantId, current.UserId, CrmModule.Entities.Quote, Guid.Empty, AuditAction.Exported,
                new JsonObject { ["count"] = items.Count, ["filters"] = string.Join('&', filters.Select(f => $"{f.Key}={f.Value}")) },
                ExecutionSource.Api, null));
            await unitOfWork.SaveChangesAsync(ct);
        }

        string[] columns =
        [
            "quoteNumber", "revisionNo", "title", "customerNameSnapshot", "status", "approvalStatus", "deliveryStatus", "currency", "listTotal", "discountTotal",
            "netTotal", "taxTotal", "grossTotal", "issueDate", "validUntil", "presentedAt", "decidedAt", "ownerId", "companyId",
        ];
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns));
        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => LeadEndpoints.Csv(item.Values[c]?.ToString()))));
        }

        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "teklifler.csv");
    }

    private static async Task<IResult> SettingsAsync(IQuotePolicyProvider provider, CancellationToken ct)
    {
        var s = await provider.GetAsync(ct);
        return Results.Ok(new { s.ApprovalDiscountPercent, s.ApprovalGrossAbove, s.DefaultValidityDays, s.AllowSelfApproval });
    }
}

/// <summary>Teklif politikasını tenant ayarlarından okur (modül ayar altyapısına erişemez).</summary>
internal sealed class QuotePolicyProvider(IServiceProvider services) : IQuotePolicyProvider
{
    public async Task<QuotePolicySettings> GetAsync(CancellationToken cancellationToken)
    {
        var settings = services.GetRequiredService<ITenantSettings>();
        decimal? Decimal(string? text) =>
            decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

        static int Days(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 0, 999) : 0;

        var validity = int.TryParse(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteDefaultValidityDays, cancellationToken),
            NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0 ? days : 30;
        return new QuotePolicySettings(
            Decimal(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteApprovalDiscountPercent, cancellationToken)),
            Decimal(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteApprovalGrossAbove, cancellationToken)),
            validity,
            string.Equals(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteAllowSelfApproval, cancellationToken), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteOnAcceptWinOpportunity, cancellationToken), "true", StringComparison.OrdinalIgnoreCase),
            Days(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteFollowUpDays, cancellationToken)),
            Days(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteExpiryReminderDays, cancellationToken)),
            string.Equals(await settings.GetValueAsync(CrmModule.SettingKeys.QuoteSyncOpportunityAmount, cancellationToken), "true", StringComparison.OrdinalIgnoreCase));
    }
}
