using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Modulix.Core.Settings;
using Modulix.Modules.Crm;
using Modulix.Modules.Crm.Leads;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// Lead uç noktaları (/api/crm/leads). CRUD diğer CRM kaynaklarıyla aynı kalıptadır; durum/sorumlu/dönüşüm değişiklikleri ayrı
/// eylem uç noktalarıdır ve her biri kendi iznini ister. Listeler, aramalar, mükerrer incelemesi, rapor ve dışa aktarma kayıt
/// boru hattından geçer: kullanıcının izin ve veri kapsamını aşan hiçbir Lead/firma/kişi bilgisi dönmez.
/// </summary>
internal static class LeadEndpoints
{
    /// <summary>Rapor/dışa aktarma bir isteğte en fazla bu kadar Lead okur (sayfa × boyut); aşılırsa <c>Truncated</c> bildirilir.</summary>
    private const int MaxRows = 5000;

    public static void Map(RouteGroupBuilder crm)
    {
        CrmEndpoints.MapResource<LeadDto, SaveLeadRequest>(crm, "leads", CrmModule.Entities.Lead,
            CrmModule.Permissions.LeadsRead, CrmModule.Permissions.LeadsUpdate,
            createPermission: CrmModule.Permissions.LeadsCreate, deletePermission: CrmModule.Permissions.LeadsDelete);

        var leads = crm.MapGroup("/leads");
        leads.MapGet("/settings", SettingsAsync).RequirePermission(CrmModule.Permissions.LeadsRead);
        leads.MapGet("/report", ReportAsync).RequirePermission(CrmModule.Permissions.LeadsRead);
        leads.MapGet("/export", ExportAsync).RequirePermission(CrmModule.Permissions.LeadsExport);
        leads.MapGet("/duplicates", DuplicatesAsync).RequirePermission(CrmModule.Permissions.LeadsRead);
        leads.MapGet("/{id:guid}/duplicates", LeadDuplicatesAsync).RequirePermission(CrmModule.Permissions.LeadsRead);

        leads.MapPost("/{id:guid}/assign", AssignAsync).RequirePermission(CrmModule.Permissions.LeadsAssign);
        leads.MapPost("/{id:guid}/start-contacting", StartContactingAsync).RequirePermission(CrmModule.Permissions.LeadsQualify);
        leads.MapPost("/{id:guid}/qualify", QualifyAsync).RequirePermission(CrmModule.Permissions.LeadsQualify);
        leads.MapPost("/{id:guid}/nurture", NurtureAsync).RequirePermission(CrmModule.Permissions.LeadsQualify);
        leads.MapPost("/{id:guid}/disqualify", DisqualifyAsync).RequirePermission(CrmModule.Permissions.LeadsDisqualify);
        leads.MapPost("/{id:guid}/reopen", ReopenAsync).RequirePermission(CrmModule.Permissions.LeadsDisqualify);
        leads.MapGet("/{id:guid}/conversion-preview", ConversionPreviewAsync).RequirePermission(CrmModule.Permissions.LeadsConvert);
        leads.MapPost("/{id:guid}/convert", ConvertAsync).RequirePermission(CrmModule.Permissions.LeadsConvert);
    }

    // ---- Eylemler

    private static async Task<IResult> AssignAsync(Guid id, AssignLeadRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.AssignAsync(id, request.OwnerId, request.Version, ct));

    private static async Task<IResult> StartContactingAsync(Guid id, LeadVersionRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.StartContactingAsync(id, request.Version, ct));

    private static async Task<IResult> QualifyAsync(Guid id, QualifyLeadRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.QualifyAsync(id, request.Version, request.Note, ct));

    private static async Task<IResult> NurtureAsync(Guid id, NurtureLeadRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.NurtureAsync(id, request.Version, request.FollowUpAt, request.Note, ct));

    private static async Task<IResult> DisqualifyAsync(Guid id, DisqualifyLeadRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.DisqualifyAsync(id, request.Version, request.Reason ?? string.Empty, request.Note, ct));

    private static async Task<IResult> ReopenAsync(Guid id, ReopenLeadRequest request, ILeadWorkflow workflow, CancellationToken ct) =>
        Lead(await workflow.ReopenAsync(id, request.Version, request.Reason ?? string.Empty, ct));

    private static IResult Lead(RecordResult<RecordData> result) =>
        result.IsSuccess ? Results.Ok(CrmEndpoints.ToDto<LeadDto>(result.Value!)) : RecordEndpoints.ToProblem(result.Error!);

    private static async Task<IResult> ConversionPreviewAsync(Guid id, ILeadConversion conversion, CancellationToken ct)
    {
        var result = await conversion.PreviewAsync(id, ct);
        if (!result.IsSuccess)
        {
            return RecordEndpoints.ToProblem(result.Error!);
        }

        var p = result.Value!;
        return Results.Ok(new LeadConversionPreviewDto(
            p.LeadId, p.Status, p.Blockers, p.CompanyName, p.CompanyKind, p.ContactFirstName, p.ContactLastName, p.OpportunityTitle,
            p.Amount, p.Currency, p.ExpectedCloseDate, p.ExistingCompanyId, p.ExistingContactId));
    }

    private static async Task<IResult> ConvertAsync(Guid id, ConvertLeadRequest request, ILeadConversion conversion, CancellationToken ct)
    {
        var result = await conversion.ConvertAsync(new LeadConversionRequest(
            id, request.Version, request.CompanyId, request.CreateCompany, request.CompanyName, request.CompanyKind, request.ContactId,
            request.CreateContact, request.ContactFirstName, request.ContactLastName, request.OpportunityTitle, request.Amount,
            request.Currency, request.ExpectedCloseDate, request.Stage), ct);
        return result.IsSuccess
            ? Results.Ok(new ConvertLeadResultDto(
                CrmEndpoints.ToDto<LeadDto>(result.Value!.Lead), result.Value.CompanyId, result.Value.ContactId, result.Value.OpportunityId,
                result.Value.CompanyCreated, result.Value.ContactCreated))
            : RecordEndpoints.ToProblem(result.Error!);
    }

    // ---- Ayarlar

    private static async Task<IResult> SettingsAsync(ITenantSettings settings, CancellationToken ct) =>
        Results.Ok(new LeadSettingsDto(await TargetHoursAsync(settings, ct)));

    private static async Task<int?> TargetHoursAsync(ITenantSettings settings, CancellationToken ct) =>
        int.TryParse(await settings.GetValueAsync(CrmModule.SettingKeys.LeadFirstContactTargetHours, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? hours
            : null;

    // ---- Olası eşleşmeler

    private static Task<IResult> DuplicatesAsync(
        string? email, string? phone, string? companyName, string? website, Guid? excludeId, IRecordPipeline pipeline, CancellationToken ct) =>
        MatchesAsync(email, phone, companyName, website, excludeId, pipeline, ct);

    private static async Task<IResult> LeadDuplicatesAsync(Guid id, IRecordPipeline pipeline, CancellationToken ct)
    {
        var lead = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Lead, id), ct);
        if (!lead.IsSuccess)
        {
            return RecordEndpoints.ToProblem(lead.Error!);
        }

        string? Text(string name) => lead.Value!.Values[name]?.GetValue<string>();
        return await MatchesAsync(Text("email"), Text("phone"), Text("companyName"), Text("website"), id, pipeline, ct);
    }

    /// <summary>
    /// Mevcut Lead, firma ve kişilerde olası eşleşmeleri toplar. Aramalar kayıt boru hattından geçer: çağıranın izni ve kapsamı dışında
    /// kalan kayıt hiç dönmez, dönenler için de yalnızca ad/numara ve eşleşme nedeni verilir. Hiçbir şey otomatik silinmez ya da
    /// birleştirilmez; aynı e-posta/telefon tek başına mükerrer kanıtı değildir (aynı kişinin farklı ihtiyacı yeni Lead olabilir).
    /// </summary>
    private static async Task<IResult> MatchesAsync(
        string? email, string? phone, string? companyName, string? website, Guid? excludeId, IRecordPipeline pipeline, CancellationToken ct)
    {
        var found = new Dictionary<(string Kind, Guid Id), (string Title, string? Status, List<string> Reasons)>();

        async Task AddAsync(string entity, string kind, string filter, string? value, string reason, Func<RecordData, string> title)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var query = new RecordQuery(entity, 1, 10, null, new Dictionary<string, string> { [filter] = value.Trim() });
            var result = await pipeline.QueryAsync(query, ct);
            if (!result.IsSuccess)
            {
                return;
            }

            foreach (var item in result.Value!.Items.Where(i => i.Id != excludeId || kind != "Lead"))
            {
                var key = (kind, item.Id);
                if (!found.TryGetValue(key, out var entry))
                {
                    entry = (title(item), item.Values["status"]?.GetValue<string>(), []);
                    found[key] = entry;
                }

                if (!entry.Reasons.Contains(reason))
                {
                    entry.Reasons.Add(reason);
                }
            }
        }

        string LeadTitle(RecordData r) => $"{r.Values["leadNumber"]?.GetValue<string>()} · {r.Values["subject"]?.GetValue<string>()}";
        string CompanyTitle(RecordData r) => r.Values["name"]?.GetValue<string>() ?? string.Empty;
        string ContactTitle(RecordData r) => $"{r.Values["firstName"]?.GetValue<string>()} {r.Values["lastName"]?.GetValue<string>()}".Trim();

        await AddAsync(CrmModule.Entities.Lead, "Lead", "email", email, "Aynı e-posta", LeadTitle);
        await AddAsync(CrmModule.Entities.Lead, "Lead", "phone", phone, "Aynı telefon", LeadTitle);
        await AddAsync(CrmModule.Entities.Lead, "Lead", "companyName", companyName, "Aynı firma adı", LeadTitle);
        await AddAsync(CrmModule.Entities.Lead, "Lead", "websiteDomain", website, "Aynı web alan adı", LeadTitle);
        await AddAsync(CrmModule.Entities.Company, "Company", "contactValue", email, "Aynı e-posta", CompanyTitle);
        await AddAsync(CrmModule.Entities.Company, "Company", "contactValue", phone, "Aynı telefon", CompanyTitle);
        await AddAsync(CrmModule.Entities.Contact, "Contact", "contactValue", email, "Aynı e-posta", ContactTitle);
        await AddAsync(CrmModule.Entities.Contact, "Contact", "contactValue", phone, "Aynı telefon", ContactTitle);

        return Results.Ok(found
            .Select(f => new LeadMatchDto(f.Key.Kind, f.Key.Id, f.Value.Title, f.Value.Status, f.Value.Reasons))
            .OrderByDescending(m => m.Reasons.Count)
            .ThenBy(m => m.Kind)
            .ToList());
    }

    // ---- Rapor

    private static async Task<IResult> ReportAsync(
        DateTimeOffset? from, DateTimeOffset? to, IRecordPipeline pipeline, ITenantSettings settings, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var end = to ?? now;
        var start = from ?? end.AddDays(-30);
        if (start >= end)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz tarih aralığı.", detail: "Başlangıç, bitişten önce olmalıdır.");
        }

        var cohort = await ReadAllAsync(pipeline, new Dictionary<string, string>
        {
            ["receivedFrom"] = Iso(start), ["receivedBefore"] = Iso(end),
        }, ct);
        var open = await ReadAllAsync(pipeline, new Dictionary<string, string> { ["open"] = "true" }, ct);
        var convertedInPeriod = await ReadAllAsync(pipeline, new Dictionary<string, string>
        {
            ["status"] = LeadStatus.Converted, ["convertedFrom"] = Iso(start), ["convertedBefore"] = Iso(end),
        }, ct);
        var failure = cohort.Error ?? open.Error ?? convertedInPeriod.Error;
        if (failure is not null)
        {
            return RecordEndpoints.ToProblem(failure);
        }

        return Results.Ok(LeadReport.Build(
            start, end, now, cohort.Items, open.Items, convertedInPeriod.Items, await TargetHoursAsync(settings, ct),
            cohort.Truncated || open.Truncated || convertedInPeriod.Truncated));
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record Rows(IReadOnlyList<RecordData> Items, bool Truncated, RecordError? Error);

    private static async Task<Rows> ReadAllAsync(IRecordPipeline pipeline, IReadOnlyDictionary<string, string> filters, CancellationToken ct)
    {
        var items = new List<RecordData>();
        for (var page = 1; items.Count < MaxRows; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Lead, page, RecordQuery.MaxPageSize, null, filters), ct);
            if (!result.IsSuccess)
            {
                return new Rows([], false, result.Error);
            }

            items.AddRange(result.Value!.Items);
            if (items.Count >= result.Value.TotalCount)
            {
                return new Rows(items, false, null);
            }
        }

        return new Rows(items, true, null);
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
            var result = await pipeline.QueryAsync(new RecordQuery(CrmModule.Entities.Lead, page, RecordQuery.MaxPageSize, search, filters), ct);
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

        // Kişisel veri çıkışı denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç).
        if (tenant.Current is { } current)
        {
            audit.Add(new AuditEntry(current.TenantId, current.UserId, CrmModule.Entities.Lead, Guid.Empty, AuditAction.Exported,
                new JsonObject { ["count"] = items.Count, ["filters"] = string.Join('&', filters.Select(f => $"{f.Key}={f.Value}")) },
                ExecutionSource.Api, null));
            await unitOfWork.SaveChangesAsync(ct);
        }

        string[] columns =
        [
            "leadNumber", "subject", "status", "priority", "firstName", "lastName", "companyName", "email", "phone", "source",
            "ownerId", "firstReceivedAt", "firstContactAt", "qualifiedAt", "convertedAt",
        ];
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns));
        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => Csv(item.Values[c]?.ToString()))));
        }

        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "leadler.csv");
    }

    // CSV enjeksiyonuna karşı formül başlatan karakterlerle başlayan hücrelerin başına kesme konur.
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
