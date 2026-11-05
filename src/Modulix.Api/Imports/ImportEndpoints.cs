using Modulix.Api.Access;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Imports;
using Modulix.Core.Modules;
using Modulix.Core.Settings;
using Modulix.Shared.Imports;

namespace Modulix.Api.Imports;

/// <summary>
/// İçe aktarma günlüğü (ADR-0033, Dilim 1): /api/imports. Yalnızca okuma. <c>core.imports.read</c> sahibi firmanın bütün
/// içe aktarmalarını, yalnızca <c>core.imports.run</c> sahibi kendi içe aktarmalarını görür; başkasının koşusu "bulunamadı"
/// döner. Tenant oturumdan belirlenir. Satır hatası ham hücre değeri içermez.
/// </summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/imports")
            .RequireAuthorization()
            .RequireAnyPermission(CoreModule.Permissions.ReadImports, CoreModule.Permissions.RunImports)
            .WithTags("Imports");

        group.MapGet("/", ListAsync);
        group.MapGet("/{runId:guid}", GetAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        string? entityKey, string? status, string? mode, Guid? userId, DateOnly? from, DateOnly? to, int? page, int? pageSize,
        IImportLog log, ICurrentAccess access, ITenantContextAccessor tenant, ITenantSettings settings, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        ImportRunStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<ImportRunStatus>(status, ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                parsedStatus = value;
            }
            else
            {
                errors["status"] = ["Durum geçersiz."];
            }
        }

        ImportRunMode? parsedMode = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (Enum.TryParse<ImportRunMode>(mode, ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                parsedMode = value;
            }
            else
            {
                errors["mode"] = ["Mod DryRun ya da Apply olmalı."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
        }

        // Günler firmanın saat dilimindedir (Y6b), denetim kaydı ekranıyla aynı.
        var zone = RegionalTime.Find(await settings.GetValueAsync(CoreModule.SettingKeys.TimeZone, ct));
        var query = new ImportRunQuery(
            entityKey,
            parsedStatus,
            parsedMode,
            userId,
            from is { } start ? RegionalTime.StartOfDay(start, zone) : null,
            to is { } end ? RegionalTime.StartOfDay(end.AddDays(1), zone) : null,
            page ?? 1,
            pageSize ?? 50);

        var result = await log.ListAsync(query, await OnlyUserAsync(access, tenant, ct), ct);
        return Results.Ok(new ImportRunPageDto([.. result.Items.Select(ToDto)], result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> GetAsync(
        Guid runId, string? severity, string? code, int? page, int? pageSize,
        IImportLog log, ICurrentAccess access, ITenantContextAccessor tenant, CancellationToken ct)
    {
        ImportIssueSeverity? parsed = null;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            if (!Enum.TryParse<ImportIssueSeverity>(severity, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["severity"] = ["Önem Error ya da Warning olmalı."] },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            parsed = value;
        }

        if (!string.IsNullOrWhiteSpace(code) && !ImportIssueCodes.IsKnown(code))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["code"] = ["Hata kodu geçersiz."] },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var detail = await log.GetAsync(
            runId, new ImportIssueQuery(parsed, string.IsNullOrWhiteSpace(code) ? null : code, page ?? 1, pageSize ?? 50),
            await OnlyUserAsync(access, tenant, ct), ct);
        return detail is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "İçe aktarma bulunamadı.")
            : Results.Ok(ToDto(detail));
    }

    /// <summary>Firmanın tamamını görme izni yoksa yalnızca çağıranın kendi koşuları görünür.</summary>
    private static async Task<Guid?> OnlyUserAsync(ICurrentAccess access, ITenantContextAccessor tenant, CancellationToken ct) =>
        await access.HasPermissionAsync(CoreModule.Permissions.ReadImports, ct) ? null : tenant.Current!.UserId;

    private static ImportRunDto ToDto(ImportRunSummary run) => new(
        run.Id,
        run.UserId,
        run.UserName,
        run.EntityKey,
        run.EntityName,
        run.FileName,
        run.FileSize,
        run.Mode.ToString(),
        run.Policy.ToString(),
        run.Status.ToString(),
        run.TotalRows,
        run.CreatedCount,
        run.UpdatedCount,
        run.SkippedCount,
        run.FailedCount,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        run.Note);

    private static ImportRunDetailDto ToDto(ImportRunDetail detail) => new(
        ToDto(detail.Run),
        [.. detail.IssueCodes.Select(c => new ImportIssueCodeCountDto(c.Code, c.Count))],
        new ImportIssuePageDto(
            [.. detail.Issues.Items.Select(i => new ImportIssueDto(i.RowNo, i.ColumnKey, i.Code, i.Severity.ToString(), i.Message))],
            detail.Issues.Page,
            detail.Issues.PageSize,
            detail.Issues.TotalCount));
}
