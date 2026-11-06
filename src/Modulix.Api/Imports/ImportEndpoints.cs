using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Api.Tenancy;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Imports;
using Modulix.Core.Contracts.Modules;
using Modulix.Core.Modules;
using Modulix.Core.Reporting;
using Modulix.Core.Settings;
using Modulix.Shared.Imports;

namespace Modulix.Api.Imports;

/// <summary>İçe aktarma uçlarının ortak servisleri ve "bu kullanıcı bu türü içe aktarabilir mi" kuralı.</summary>
public sealed class ImportContext(
    ModuleCatalog catalog, ImportSchema schema, ImportRunner runner, ImportErrorReports reports, IImportLog log, ICurrentAccess access,
    IModuleLicense license, ITenantContextAccessor tenant, ICompanyContextAccessor company, IImportDesignStore designs)
{
    public ModuleCatalog Catalog => catalog;

    public ImportSchema Schema => schema;

    public ImportRunner Runner => runner;

    public ImportErrorReports Reports => reports;

    public IImportLog Log => log;

    public ITenantContextAccessor Tenant => tenant;

    public ICompanyContextAccessor Company => company;

    /// <summary>Modül yazılabilir (ADR-0028: salt okunurda tür listelenmez), tür firmanın tasarımında açık olmalı ve kullanıcının türde oluşturma izni bulunmalı.</summary>
    public async Task<bool> CanRunAsync(string moduleKey, ImportDefinition definition, CancellationToken cancellationToken) =>
        await license.AllowsAsync(tenant.Current!.TenantId, moduleKey, write: true, cancellationToken)
        && await access.HasPermissionAsync(definition.CreatePermission, cancellationToken)
        && (await designs.GetAsync(definition.EntityKey, cancellationToken)).Enabled;
}

/// <summary>
/// İçe aktarma (ADR-0033): /api/imports. <c>core.imports.read</c> sahibi firmanın bütün içe aktarmalarını, yalnızca
/// <c>core.imports.run</c> sahibi kendi içe aktarmalarını görür; başkasının koşusu "bulunamadı" döner. Başlatmak için
/// <c>core.imports.run</c> ve türün oluşturma izni gerekir. Tenant oturumdan belirlenir. Satır hatası ham hücre değeri içermez.
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

        // Başlatma (Dilim 2): dosya gövdede ham bayttır, tür ve ad sorgudadır. Satırlar arka planda işlenir: 202 döner, koşu GET ile izlenir. Modül hakkı türe göre değiştiği için RequireModule yerine
        // uçta denetlenir. Kuru çalışma iş kaydı yazmaz; yalnızca günlüğe koşu satırı ekler.
        group.MapGet("/definitions", ListDefinitionsAsync).RequirePermission(CoreModule.Permissions.RunImports);
        group.MapGet("/definitions/{entityKey}/template", GetTemplateAsync).RequirePermission(CoreModule.Permissions.RunImports);
        group.MapPost("/preview", (string? entityKey, string? fileName, HttpRequest http, ImportContext context, CancellationToken ct) =>
            StartAsync(ImportRunMode.DryRun, entityKey, fileName, http, context, ct)).RequirePermission(CoreModule.Permissions.RunImports);
        group.MapPost("/", (string? entityKey, string? fileName, HttpRequest http, ImportContext context, CancellationToken ct) =>
            StartAsync(ImportRunMode.Apply, entityKey, fileName, http, context, ct)).RequirePermission(CoreModule.Permissions.RunImports);
        group.MapGet("/{runId:guid}/error-report", GetErrorReportAsync);

        // İçe aktarma tasarımı: firma yöneticisi türleri açıp kapatır, sütunları seçer (form tasarımıyla aynı izin).
        var design = app.MapGroup("/api/customization/import-designs")
            .RequireAuthorization()
            .AddEndpointFilter<RequireTenantFilter>()
            .RequirePermission(CoreModule.Permissions.ManageCustomization)
            .WithTags("Customization");
        design.MapGet("/", async (IImportDesignService service, CancellationToken ct) => (await service.ListAsync(ct)).Select(ToDto).ToList());
        design.MapPut("/{entityKey}", async (string entityKey, SaveImportDesignRequest request, IImportDesignService service, CancellationToken ct) =>
        {
            var input = new ImportDesignInput(request.Enabled, request.RunAutomation, request.Excluded ?? [], request.Required ?? []);
            var result = await service.SaveAsync(entityKey, input, request.Version, ct);
            return result.IsSuccess ? Results.Ok(ToDto(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);
        });
        return app;
    }

    private static async Task<IResult> ListDefinitionsAsync(ImportContext context, CancellationToken ct)
    {
        var result = new List<ImportDefinitionDto>();
        foreach (var (moduleKey, definition) in context.Catalog.ImportDefinitions)
        {
            if (await context.CanRunAsync(moduleKey, definition, ct))
            {
                var fields = await context.Schema.FieldsAsync(context.Tenant.Current!, definition, ct);
                result.Add(new ImportDefinitionDto(
                    definition.EntityKey, definition.DisplayName, [.. fields.Select(f => new ImportFieldDto(f.Key, f.Label, f.Required))]));
            }
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> GetTemplateAsync(string entityKey, ImportContext context, CancellationToken ct)
    {
        if (context.Catalog.FindImport(entityKey) is not { } found || !await context.CanRunAsync(found.ModuleKey, found.Definition, ct))
        {
            return NotImportable();
        }

        var fields = await context.Schema.FieldsAsync(context.Tenant.Current!, found.Definition, ct);
        return Results.File(ImportErrorReport.Template(fields), "text/csv", ReportCsv.FileSlug(found.Definition.DisplayName) + "-sablon.csv");
    }

    private static async Task<IResult> StartAsync(
        ImportRunMode mode, string? entityKey, string? fileName, HttpRequest http, ImportContext context, CancellationToken ct)
    {
        // Kapalı ya da salt okunur modülün türü ve izinsiz tür listede yoktur; aynı cevabı alır.
        if (entityKey is null || context.Catalog.FindImport(entityKey) is not { } found || !await context.CanRunAsync(found.ModuleKey, found.Definition, ct))
        {
            return NotImportable();
        }

        var name = Path.GetFileName(fileName ?? string.Empty);
        if (Path.GetExtension(name).ToLowerInvariant() is not (".csv" or ".txt"))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Şimdilik yalnızca .csv dosyası yüklenebilir; Excel'de 'CSV' olarak kaydedin.");
        }

        var (content, problem) = await LimitedBody.ReadAsync(http, ImportLimits.MaxFileBytes, ct);
        if (content is null)
        {
            return problem!;
        }

        var runId = await context.Runner.StartAsync(found.Definition, mode, name, content, context.Tenant.Current!, context.Company.Current);
        if (runId is not { } id)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Bu firmada başka bir içe aktarma sürüyor; bitince yeniden deneyin.");
        }

        var detail = await context.Log.GetAsync(id, new ImportIssueQuery(), context.Tenant.Current!.UserId, ct);
        return Results.Accepted($"/api/imports/{id}", ToDto(detail!));
    }

    /// <summary>Rapor yükleyenin dosyasından üretilir ve hassas değer taşıyabilir: firmanın günlüğünü görenlere değil, yalnızca yükleyene verilir.</summary>
    private static async Task<IResult> GetErrorReportAsync(Guid runId, ImportContext context, CancellationToken ct)
    {
        var tenant = context.Tenant.Current!;
        if (await context.Log.GetAsync(runId, new ImportIssueQuery(PageSize: 1), tenant.UserId, ct) is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "İçe aktarma bulunamadı.");
        }

        return context.Reports.Find(tenant.TenantId, runId) is { } report
            ? Results.File(report.Content, "text/csv", report.FileName)
            : Results.Problem(statusCode: StatusCodes.Status410Gone, title: "Hata raporunun süresi doldu; dosyayı yeniden yükleyerek yeni rapor alın.");
    }

    private static ImportDesignDto ToDto(ImportDesignInfo info) => new(
        info.EntityKey,
        info.DisplayName,
        info.Enabled,
        info.RunAutomation,
        [.. info.Fields.Select(f => new ImportDesignFieldDto(f.Key, f.Label, f.Included, f.Required, f.Locked))],
        info.Version);

    private static IResult NotImportable() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Bu kayıt türü içe aktarılamıyor.");

    private static async Task<IResult> ListAsync(
        string? entityKey, string? status, string? mode, Guid? userId, DateOnly? from, DateOnly? to, string? sort, int? page, int? pageSize,
        IImportLog log, ICurrentAccess access, ITenantContextAccessor tenant, ITenantSettings settings, CancellationToken ct)
    {
        // Başlığa göre sıralama (ADR-0036): yalnızca bilinen anahtarlar kabul edilir.
        if (!string.IsNullOrEmpty(sort) && !ImportRunQuery.SortKeys.Contains(sort))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["sort"] = ["Geçersiz sıralama."] }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

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
            pageSize ?? 50,
            string.IsNullOrEmpty(sort) ? null : sort);

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
