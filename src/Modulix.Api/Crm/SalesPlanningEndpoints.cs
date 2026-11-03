using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Reporting;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Satış raporlama ve tahmin uç noktaları (ADR-0026): /api/crm/sales/overview, /api/crm/sales-targets, /api/crm/sales/forecasts. Hepsi kayıt boru hattından geçer:
/// izin, veri kapsamı ve hassas alan kuralları fırsat listesiyle aynıdır; tenant istemciden değil oturumdan gelir. Tahmin eylemleri ayrı izinlerdir.
/// </summary>
internal static class SalesPlanningEndpoints
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<SalesTargetDto, SaveSalesTargetRequest>(crm, "sales-targets", CrmModule.Entities.SalesTarget,
            CrmModule.Permissions.SalesTargetsRead, CrmModule.Permissions.SalesTargetsManage);

        var sales = crm.MapGroup("/sales");
        sales.MapGet("/overview", OverviewAsync).RequirePermission(CrmModule.Permissions.SalesReportsRead);
        sales.MapGet("/overview/cards/{card}", CardAsync).RequirePermission(CrmModule.Permissions.SalesReportsRead);

        var analytics = sales.MapGroup("/analytics");
        analytics.MapGet("/quotes", (string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency, ISalesAnalytics analysis, CancellationToken ct) =>
            AnalyzeAsync<QuoteCohortResult, AnalyticsDto<QuoteCohortDto>>(kind, date, asOf, ownerId, currency, analysis.QuoteCohortAsync, ct)).RequirePermission(CrmModule.Permissions.SalesReportsRead);
        analytics.MapGet("/leads", (string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency, ISalesAnalytics analysis, CancellationToken ct) =>
            AnalyzeAsync<LeadCohortResult, AnalyticsDto<LeadCohortDto>>(kind, date, asOf, ownerId, currency, analysis.LeadCohortAsync, ct)).RequirePermission(CrmModule.Permissions.SalesReportsRead);
        analytics.MapGet("/stages", (string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency, ISalesAnalytics analysis, CancellationToken ct) =>
            AnalyzeAsync<StageFlowResult, AnalyticsDto<StageFlowDto>>(kind, date, asOf, ownerId, currency, analysis.StageFlowAsync, ct)).RequirePermission(CrmModule.Permissions.SalesReportsRead);
        analytics.MapGet("/team", (string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency, ISalesAnalytics analysis, CancellationToken ct) =>
            AnalyzeAsync<IReadOnlyList<TeamPerformanceRow>, AnalyticsDto<List<TeamPerformanceRowDto>>>(kind, date, asOf, ownerId, currency, analysis.TeamPerformanceAsync, ct)).RequirePermission(CrmModule.Permissions.SalesReportsRead);
        analytics.MapGet("/quality", (string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency, ISalesAnalytics analysis, CancellationToken ct) =>
            AnalyzeAsync<DataQualityData, AnalyticsDto<DataQualityDto>>(kind, date, asOf, ownerId, currency, analysis.DataQualityAsync, ct)).RequirePermission(CrmModule.Permissions.SalesReportsRead);

        var forecasts = sales.MapGroup("/forecasts");
        forecasts.MapGet("", ListAsync).RequirePermission(CrmModule.Permissions.ForecastsRead);
        forecasts.MapGet("/{id:guid}", GetAsync).RequirePermission(CrmModule.Permissions.ForecastsRead);
        forecasts.MapGet("/{id:guid}/compare", CompareAsync).RequirePermission(CrmModule.Permissions.ForecastsRead);
        forecasts.MapPost("", CreateAsync).RequirePermission(CrmModule.Permissions.ForecastsWrite);
        forecasts.MapPost("/{id:guid}/refresh", RefreshAsync).RequirePermission(CrmModule.Permissions.ForecastsWrite);
        forecasts.MapPost("/{id:guid}/adjust", AdjustAsync).RequirePermission(CrmModule.Permissions.ForecastsWrite);
        forecasts.MapPut("/{id:guid}/note", NoteAsync).RequirePermission(CrmModule.Permissions.ForecastsWrite);
        forecasts.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(CrmModule.Permissions.ForecastsWrite);
        forecasts.MapPost("/{id:guid}/submit", SubmitAsync).RequirePermission(CrmModule.Permissions.ForecastsSubmit);
        forecasts.MapPost("/{id:guid}/return", ReturnAsync).RequirePermission(CrmModule.Permissions.ForecastsSubmit);
        forecasts.MapPost("/{id:guid}/lock", LockAsync).RequirePermission(CrmModule.Permissions.ForecastsLock);
        forecasts.MapPost("/{id:guid}/reopen", ReopenAsync).RequirePermission(CrmModule.Permissions.ForecastsReopen);
    }

    // ---- Genel görünüm

    private static async Task<IResult> OverviewAsync(string? kind, DateOnly? date, Guid? ownerId, string? currency, ISalesOverview overview, CancellationToken ct)
    {
        if (!TryKind(kind, out var periodKind))
        {
            return InvalidKind();
        }

        var result = await overview.BuildAsync(new SalesOverviewQuery(periodKind, date, ownerId, currency), ct);
        return result.IsSuccess ? Results.Ok(Map<SalesOverviewDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> CardAsync(string card, string? kind, DateOnly? date, Guid? ownerId, string? currency, ISalesOverview overview, CancellationToken ct)
    {
        if (!Enum.TryParse<SalesCard>(card, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || char.IsDigit(card[0]))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["card"] = [$"Şunlardan biri olmalıdır: {string.Join(", ", Enum.GetNames<SalesCard>())}."] }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (!TryKind(kind, out var periodKind))
        {
            return InvalidKind();
        }

        var result = await overview.DetailAsync(parsed, new SalesOverviewQuery(periodKind, date, ownerId, currency), ct);
        return result.IsSuccess ? Results.Ok(Map<SalesCardDetailDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Analizler

    private static async Task<IResult> AnalyzeAsync<TData, TDto>(
        string? kind, DateOnly? date, DateOnly? asOf, Guid? ownerId, string? currency,
        Func<AnalyticsQuery, CancellationToken, Task<RecordResult<AnalyticsResult<TData>>>> analyze, CancellationToken ct)
    {
        if (!TryKind(kind, out var periodKind))
        {
            return InvalidKind();
        }

        var result = await analyze(new AnalyticsQuery(periodKind, date, asOf, ownerId, currency), ct);
        return result.IsSuccess ? Results.Ok(Map<TDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Tahmin

    private static async Task<IResult> ListAsync(DateOnly? from, DateOnly? to, string? status, IForecastWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.ListAsync(from, to, status, ct);
        return result.IsSuccess ? Results.Ok(Map<List<ForecastHeaderDto>>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> GetAsync(Guid id, IForecastWorkflow workflow, CancellationToken ct) => Detail(await workflow.GetAsync(id, ct));

    private static async Task<IResult> CompareAsync(Guid id, Guid? against, IForecastWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.CompareAsync(id, against, ct);
        return result.IsSuccess ? Results.Ok(Map<ForecastComparisonDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> CreateAsync(CreateForecastRequestDto request, IForecastWorkflow workflow, CancellationToken ct)
    {
        if (!TryKind(request.Kind, out var kind))
        {
            return InvalidKind();
        }

        var result = await workflow.CreateAsync(new CreateForecastRequest(kind, request.Anchor, request.ScopeType, request.ScopeUserId, request.Currency), ct);
        return result.IsSuccess ? Results.Created($"/api/crm/sales/forecasts/{result.Value!.Header.Id}", Map<ForecastDetailDto>(result.Value)) : RecordEndpoints.ToProblem(result.Error!);
    }

    private static async Task<IResult> RefreshAsync(Guid id, ForecastVersionRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.RefreshAsync(id, request.Version, ct));

    private static async Task<IResult> AdjustAsync(Guid id, AdjustForecastRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.AdjustAsync(id, request.ItemId, request.Kind, request.Value, request.Reason, request.Version, ct));

    private static async Task<IResult> NoteAsync(Guid id, ForecastNoteRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.SetNoteAsync(id, request.Note, request.Version, ct));

    private static async Task<IResult> SubmitAsync(Guid id, ForecastVersionRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.SubmitAsync(id, request.Version, ct));

    private static async Task<IResult> ReturnAsync(Guid id, ForecastReasonRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.ReturnToDraftAsync(id, request.Reason, request.Version, ct));

    private static async Task<IResult> LockAsync(Guid id, ForecastVersionRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.LockAsync(id, request.Version, ct));

    private static async Task<IResult> ReopenAsync(Guid id, ForecastReasonRequestDto request, IForecastWorkflow workflow, CancellationToken ct) =>
        Detail(await workflow.ReopenAsync(id, request.Reason, ct));

    private static async Task<IResult> DeleteAsync(Guid id, long? version, IForecastWorkflow workflow, CancellationToken ct)
    {
        var result = await workflow.DeleteDraftAsync(id, version, ct);
        return result.IsSuccess ? Results.NoContent() : RecordEndpoints.ToProblem(result.Error!);
    }

    private static IResult Detail(RecordResult<ForecastDetail> result) =>
        result.IsSuccess ? Results.Ok(Map<ForecastDetailDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    // ---- Yardımcılar

    private static bool TryKind(string? kind, out PeriodKind parsed)
    {
        parsed = PeriodKind.Month;
        return string.IsNullOrEmpty(kind) || (Enum.TryParse(kind, ignoreCase: true, out parsed) && Enum.IsDefined(parsed) && !char.IsDigit(kind[0]));
    }

    private static IResult InvalidKind() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = [$"Şunlardan biri olmalıdır: {string.Join(", ", SalesPeriodKinds.All)}."] }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static T Map<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

/// <summary>Satış raporlama politikasını tenant ayarlarından okur (modül ayar altyapısına erişemez).</summary>
internal sealed class SalesReportingPolicyProvider(IServiceProvider services) : ISalesReportingPolicyProvider
{
    public async Task<SalesReportingPolicy> GetAsync(CancellationToken cancellationToken)
    {
        // Ayar servisi kayıt boru hattını kullanır; kayıt depoları bu sağlayıcıya bağlı olabileceğinden çözümleme gecikmelidir (döngüyü önler).
        var settings = services.GetRequiredService<ITenantSettings>();
        var zone = RegionalTime.Find(await settings.GetValueAsync(CoreModule.SettingKeys.TimeZone, cancellationToken));
        var currency = (await settings.GetValueAsync(CoreModule.SettingKeys.Currency, cancellationToken)) is { Length: 3 } code ? code.ToUpperInvariant() : RegionalOptions.DefaultCurrency;

        async Task<int> Number(string key, int fallback, int min, int max) =>
            int.TryParse(await settings.GetValueAsync(key, cancellationToken), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max ? value : fallback;

        var commit = await Number(CrmModule.SettingKeys.ForecastCommitMinProbability, ForecastPolicy.Default.CommitMinProbability, 1, 100);
        var bestCase = await Number(CrmModule.SettingKeys.ForecastBestCaseMinProbability, ForecastPolicy.Default.BestCaseMinProbability, 1, 100);
        var fiscal = await Number(CrmModule.SettingKeys.FiscalYearStartMonth, ReportPeriods.DefaultFiscalStartMonth, 1, 12);
        // Taahhüt eşiği En İyi Durum eşiğinin altına inemez (tutarsız ayar sessizce düzeltilir).
        return new SalesReportingPolicy(zone, fiscal, new ForecastPolicy(Math.Max(commit, bestCase), bestCase), currency);
    }
}
