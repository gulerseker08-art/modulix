using System.Text.Json;
using Modulix.Api.Access;
using Modulix.Core.Contracts.Reporting;
using Modulix.Core.Modules;
using Modulix.Core.Reporting;
using Modulix.Shared.Reporting;

namespace Modulix.Api.Reports;

/// <summary>
/// Ortak rapor merkezi (ADR-0026, dilim 3). Rapor merkezini açmak ve çalıştırmak <c>core.reports.read</c>, kendi raporlarını yönetmek
/// <c>core.reports.manage</c>, paylaşmak <c>core.reports.share</c> ister. İzinler <b>veri erişimi vermez</b>: her çalıştırma çağıranın kayıt
/// izni, veri kapsamı ve hassas alan kurallarıyla yapılır; paylaşılan rapor yalnızca tanımı görünür kılar. Tenant oturumdan belirlenir,
/// başka tenant'ın raporu "bulunamadı" döner. Çalıştırma sonucu domain durumunu <c>status</c> alanında taşır (HTTP 200).
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization().WithTags("Reports");

        group.MapGet("/catalog", async (IReportCatalog catalog, CancellationToken ct) => Results.Ok(ToDto(await catalog.GetAsync(ct))))
            .RequirePermission(CoreModule.Permissions.ReadReports);

        group.MapGet("/", async (string? scope, string? search, int? page, int? pageSize, IReportCenter center, CancellationToken ct) =>
        {
            if (!TryScope(scope, out var parsed))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["scope"] = ["Kapsam all, mine, shared ya da favorites olmalı."] }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await center.ListAsync(new ReportListQuery(parsed, search, page ?? 1, pageSize ?? 50), ct);
            return Results.Ok(new ReportPageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
        }).RequirePermission(CoreModule.Permissions.ReadReports);

        group.MapGet("/{id:guid}", async (Guid id, IReportCenter center, CancellationToken ct) =>
            await center.GetAsync(id, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound())
            .RequirePermission(CoreModule.Permissions.ReadReports);

        group.MapPost("/", (SaveReportRequest request, IReportCenter center, CancellationToken ct) => RunAsync(async () =>
        {
            var detail = await center.CreateAsync(ToInput(request), ct);
            return Results.Created($"/api/reports/{detail.Summary.Id}", ToDto(detail));
        })).RequirePermission(CoreModule.Permissions.ManageReports);

        group.MapPut("/{id:guid}", (Guid id, SaveReportRequest request, IReportCenter center, CancellationToken ct) => RunAsync(async () =>
            await center.UpdateAsync(id, ToInput(request), request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ManageReports);

        group.MapDelete("/{id:guid}", (Guid id, IReportCenter center, CancellationToken ct) => RunAsync(async () =>
            await center.DeleteAsync(id, ct) ? Results.NoContent() : NotFound()))
            .RequirePermission(CoreModule.Permissions.ManageReports);

        group.MapPut("/{id:guid}/sharing", (Guid id, ShareReportRequest request, IReportCenter center, CancellationToken ct) => RunAsync(async () =>
        {
            if (!Enum.TryParse<ReportVisibility>(request.Visibility, ignoreCase: true, out var visibility))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["visibility"] = ["Paylaşım private, users ya da tenant olmalı."] }, statusCode: StatusCodes.Status400BadRequest);
            }

            return await center.ShareAsync(id, visibility, request.Users ?? [], request.ExpectedVersion, ct) is { } detail ? Results.Ok(ToDto(detail)) : NotFound();
        })).RequirePermission(CoreModule.Permissions.ShareReports);

        group.MapPut("/{id:guid}/favorite", async (Guid id, FavoriteReportRequest request, IReportCenter center, CancellationToken ct) =>
            await center.SetFavoriteAsync(id, request.Favorite, ct) is { } favorite ? Results.Ok(new { favorite }) : NotFound())
            .RequirePermission(CoreModule.Permissions.ReadReports);

        group.MapPost("/run", (RunReportRequest request, IReportRunner runner, CancellationToken ct) => RunAsync(async () =>
        {
            if (request.Spec is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Rapor tanımı gerekli."] }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await runner.RunAsync(new ReportRunRequest(ToSpec(request.Spec), ToDrill(request.Drill), request.Page, request.PageSize), ct);
            return Results.Ok(ToDto(result));
        })).RequirePermission(CoreModule.Permissions.ReadReports);

        group.MapPost("/{id:guid}/run", (Guid id, RunReportRequest request, IReportCenter center, CancellationToken ct) => RunAsync(async () =>
            await center.RunAsync(id, ToDrill(request.Drill), request.Page, request.PageSize, ct) is { } result ? Results.Ok(ToDto(result)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ReadReports);

        MapExports(group);
        MapSchedules(group);
        return app;
    }

    /// <summary>
    /// Dışa aktarma (<c>core.reports.export</c>): CSV, sahibine özel, 24 saat. İndirme anında yetki yeniden denetlenir; dosya yalnızca sahibine verilir.
    /// Hata kodları: 404 yok/başkasının, 410 süresi doldu, 403 yetki yok (modül kapalı dahil), 409 yetki dosya hazırlandıktan sonra değişti.
    /// </summary>
    private static void MapExports(RouteGroupBuilder group)
    {
        group.MapGet("/exports", async (IReportExports exports, CancellationToken ct) =>
            Results.Ok((await exports.ListAsync(ct)).Select(ToDto).ToList()))
            .RequirePermission(CoreModule.Permissions.ExportReports);

        group.MapPost("/exports", (ExportReportRequest request, string? format, IReportExports exports, CancellationToken ct) => RunAsync(async () =>
        {
            if (request.Spec is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["spec"] = ["Rapor tanımı gerekli."] }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!TryFormat(format, out var fileFormat))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Biçim csv ya da xlsx olmalıdır."] }, statusCode: StatusCodes.Status400BadRequest);
            }

            var export = await exports.CreateAsync(ToSpec(request.Spec), request.Name, ct, fileFormat);
            return Results.Created($"/api/reports/exports/{export.Id}", ToDto(export));
        })).RequirePermission(CoreModule.Permissions.ExportReports);

        group.MapPost("/{id:guid}/exports", (Guid id, string? format, IReportExports exports, CancellationToken ct) => RunAsync(async () =>
            !TryFormat(format, out var fileFormat) ? Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Biçim csv ya da xlsx olmalıdır."] }, statusCode: StatusCodes.Status400BadRequest)
            : await exports.CreateAsync(id, ct, fileFormat) is { } export ? Results.Created($"/api/reports/exports/{export.Id}", ToDto(export)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ExportReports);

        group.MapGet("/exports/{id:guid}/download", async (Guid id, HttpContext http, IReportExports exports, CancellationToken ct) =>
        {
            var download = await exports.DownloadAsync(id, ct);
            switch (download.Status)
            {
                case ReportDownloadStatus.Ok:
                    // Dosya önbelleğe alınmaz, içerik türü sezilmez; her zaman indirme olarak sunulur.
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.Headers.XContentTypeOptions = "nosniff";
                    return Results.File(download.Content!, download.ContentType, download.FileName);
                case ReportDownloadStatus.Expired:
                    return Results.Problem(statusCode: StatusCodes.Status410Gone, title: download.Message);
                case ReportDownloadStatus.Forbidden:
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: download.Message);
                case ReportDownloadStatus.AccessChanged:
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: download.Message);
                default:
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: download.Message);
            }
        }).RequirePermission(CoreModule.Permissions.ExportReports);

        group.MapDelete("/exports/{id:guid}", async (Guid id, IReportExports exports, CancellationToken ct) =>
            await exports.DeleteAsync(id, ct) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Dosya bulunamadı."))
            .RequirePermission(CoreModule.Permissions.ExportReports);
    }

    /// <summary>
    /// Zamanlanmış rapor (<c>core.reports.schedule</c>): kullanıcı yalnızca kendi zamanlamalarını görür ve yönetir. Çalışma anında sahibin yetkisi yeniden
    /// denetlenir; yetki kaybında zamanlama durdurulur ve hiçbir şey teslim edilmez (<c>stoppedReason</c>).
    /// </summary>
    private static void MapSchedules(RouteGroupBuilder group)
    {
        group.MapGet("/schedules", async (Guid? reportId, IReportSchedules schedules, CancellationToken ct) =>
            Results.Ok((await schedules.ListAsync(reportId, ct)).Select(ToDto).ToList()))
            .RequirePermission(CoreModule.Permissions.ScheduleReports);

        group.MapPost("/{id:guid}/schedules", (Guid id, SaveReportScheduleRequest request, IReportSchedules schedules, CancellationToken ct) => RunAsync(async () =>
            await schedules.CreateAsync(id, ToInput(request), ct) is { } schedule ? Results.Created($"/api/reports/schedules/{schedule.Id}", ToDto(schedule)) : NotFound()))
            .RequirePermission(CoreModule.Permissions.ScheduleReports);

        group.MapPut("/schedules/{id:guid}", (Guid id, SaveReportScheduleRequest request, IReportSchedules schedules, CancellationToken ct) => RunAsync(async () =>
            await schedules.UpdateAsync(id, ToInput(request), request.ExpectedVersion, ct) is { } schedule ? Results.Ok(ToDto(schedule)) : ScheduleNotFound()))
            .RequirePermission(CoreModule.Permissions.ScheduleReports);

        group.MapPut("/schedules/{id:guid}/enabled", (Guid id, SetReportScheduleEnabledRequest request, IReportSchedules schedules, CancellationToken ct) => RunAsync(async () =>
            await schedules.SetEnabledAsync(id, request.Enabled, ct) is { } schedule ? Results.Ok(ToDto(schedule)) : ScheduleNotFound()))
            .RequirePermission(CoreModule.Permissions.ScheduleReports);

        group.MapDelete("/schedules/{id:guid}", (Guid id, IReportSchedules schedules, CancellationToken ct) => RunAsync(async () =>
            await schedules.DeleteAsync(id, ct) ? Results.NoContent() : ScheduleNotFound()))
            .RequirePermission(CoreModule.Permissions.ScheduleReports);
    }

    private static IResult ScheduleNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Zamanlama bulunamadı.");

    private static ReportScheduleInput ToInput(SaveReportScheduleRequest request)
    {
        if (!Enum.TryParse<ScheduleFrequency>(request.Frequency, ignoreCase: true, out var frequency))
        {
            throw new ReportException("Sıklık daily, weekly ya da monthly olmalı.");
        }

        DayOfWeek? day = null;
        if (!string.IsNullOrWhiteSpace(request.DayOfWeek))
        {
            day = Enum.TryParse<DayOfWeek>(request.DayOfWeek, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ReportException("Haftanın günü sunday..saturday olmalı.");
        }

        if (!TryFormat(request.Format, out var format))
        {
            throw new ReportException("Biçim csv ya da xlsx olmalı.");
        }

        return new ReportScheduleInput(frequency, request.Hour, day, request.DayOfMonth, format);
    }

    /// <summary>Biçim sorgu değeri: boş csv'dir; yalnızca csv ve xlsx kabul edilir.</summary>
    private static bool TryFormat(string? value, out ReportFileFormat format)
    {
        format = ReportFileFormat.Csv;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            format = ReportFileFormat.Xlsx;
            return true;
        }

        return false;
    }

    private static ReportExportDto ToDto(ReportExportInfo e) => new(
        e.Id, e.ReportId, e.ReportName, e.DatasetKey, e.FileName, e.SizeBytes, e.RowCount, e.Truncated, Name(e.Source), e.CreatedAt, e.ExpiresAt, e.DownloadCount, e.Expired);

    private static ReportScheduleDto ToDto(ReportScheduleInfo s) => new(
        s.Id, s.ReportId, s.ReportName, Name(s.Frequency), s.Hour, s.DayOfWeek is { } day ? Name(day) : null, s.DayOfMonth, s.Enabled, s.NextRunAt, s.LastRunAt,
        s.LastStatus, s.LastMessage, s.StoppedReason, s.Version, s.Format == ReportFileFormat.Xlsx ? "xlsx" : "csv");

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ReportException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: ex.Message,
                extensions: new Dictionary<string, object?> { ["issues"] = ex.Issues.Select(ToDto).ToList() });
        }
        catch (ReportConflictException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
        }
        catch (ReportForbiddenException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["spec"] = ["Rapor tanımı okunamadı: bilinmeyen işleç, toplama ya da değer."] },
                title: "Lütfen rapor tanımını düzeltin.",
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Rapor bulunamadı.");

    private static bool TryScope(string? scope, out ReportListScope parsed)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            parsed = ReportListScope.All;
            return true;
        }

        return Enum.TryParse(scope, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    // ---- Eşleme

    private static ReportInput ToInput(SaveReportRequest request) =>
        new(request.Name, request.Description, request.Spec is null ? throw new JsonException("Rapor tanımı gerekli.") : ToSpec(request.Spec));

    /// <summary>Tanım DTO'su ile çekirdek tanımı aynı JSON biçimini paylaşır (sabit değerler camelCase metin); eşleme JSON üzerinden yapılır.</summary>
    internal static ReportSpec ToSpec(ReportSpecDto dto) => ReportSpecJson.Deserialize(JsonSerializer.Serialize(dto, ReportSpecJson.Options));

    internal static ReportSpecDto ToDto(ReportSpec spec) =>
        JsonSerializer.Deserialize<ReportSpecDto>(ReportSpecJson.Serialize(spec), ReportSpecJson.Options) ?? new ReportSpecDto();

    private static ReportDrill? ToDrill(ReportDrillDto? dto) => dto is null ? null : new ReportDrill(dto.Keys ?? [], dto.Currency);

    private static string Name(Enum value) => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static IReadOnlyList<string> UseNames(ColumnUse use) =>
        [.. Enum.GetValues<ColumnUse>().Where(u => u != ColumnUse.None && use.HasFlag(u)).Select(u => Name(u))];

    private static ReportCatalogDto ToDto(ReportCatalogView view) => new(
        [.. view.Datasets.Select(d => new ReportDatasetDto(
            d.Key, d.ModuleKey, d.Name, d.Version, d.Available, d.UnavailableReason,
            [.. d.Columns.Select(c => new ReportColumnInfoDto(
                c.Field, c.Label, Name(c.Kind), UseNames(c.Use), c.Custom, c.CurrencyField, c.Options?.Select(o => new ReportOptionDto(o.Key, o.Label)).ToList()))],
            d.DefaultColumns,
            [.. d.ReadyReports.Select(r => new ReadyReportDto(r.Key, r.Title, r.Description, ToDto(r.Spec)))],
            d.RecordPath))],
        [.. view.Links.Select(l => new ReportLinkDto(l.Key, l.ModuleKey, l.Title, l.Description, l.Path))]);

    private static ReportSummaryDto ToDto(ReportSummary s) => new(
        s.Id, s.Name, s.Description, s.DatasetKey, s.DatasetName, Name(s.Visibility), s.OwnerUserId, s.OwnerName, s.IsOwner, s.IsFavorite,
        s.SharedWithCount, s.UpdatedAt, s.Version, Name(s.Health), s.HealthMessage);

    private static ReportDetailDto ToDto(ReportDetail d) => new(ToDto(d.Summary), ToDto(d.Spec), d.DatasetVersion, d.SharedWith);

    private static ReportIssueDto ToDto(ReportIssue i) => new(Name(i.Code), i.Field, i.Message, i.Blocking);

    private static ReportResultDto ToDto(ReportResult r) => new(
        Name(r.Status), r.IsSummary,
        [.. r.Columns.Select(c => new ReportResultColumnDto(c.Key, c.Label, Name(c.Kind), Name(c.Role), c.ExcludedRows))],
        [.. r.Rows.Select(ToDto)], [.. r.Totals.Select(ToDto)], r.RowsRead, r.RowsMatched, r.Truncated, r.RowsLimited, r.Page, r.PageSize, r.GeneratedAt,
        r.DatasetVersion, [.. r.Issues.Select(ToDto)], r.DatasetName, r.RecordPath, Name(r.Chart));

    private static ReportResultRowDto ToDto(ReportResultRow row) => new(
        [.. row.Cells.Select(c => JsonSerializer.SerializeToElement(c))], row.Keys, row.Currency, row.RecordId);
}
