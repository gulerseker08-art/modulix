using System.Text;
using System.Text.Json.Nodes;
using Modulix.Api.Access;
using Modulix.Api.Crm;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Persistence;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Lookups;
using Modulix.Core.Records;
using Modulix.Modules.Contracts;
using Modulix.Modules.Contracts.Lifecycle;
using Modulix.Shared.Contracts;

namespace Modulix.Api.Contracts;

/// <summary>
/// Pano ve dışa aktarma uç noktaları (ADR-0022, dilim 9). İkisi de kayıt boru hattından okur: izin, veri kapsamı, gizlilik ve tutar maskesi liste ekranıyla aynıdır.
/// Dışa aktarma ayrı bir izne (<c>contracts.contracts.export</c>) bağlıdır; çıkış denetim kaydına yazılır.
/// </summary>
internal static class ContractViewEndpoints
{
    private const int ExportMaxRows = 5000;

    private static readonly string[] ExportColumns =
    [
        "number", "title", "type", "customer", "preparationStatus", "approvalStatus", "signatureStatus", "effectiveStatus", "startDate", "endDate", "isIndefinite", "noticeDeadline",
        "renewalMode", "currency", "netTotal", "taxTotal", "grossTotal", "ownerId", "currentVersionNo",
    ];

    private static readonly HashSet<string> MoneyColumns = ["netTotal", "taxTotal", "grossTotal"];

    public static void MapInternal(RouteGroupBuilder group)
    {
        group.MapGet("/dashboard", async (IContractDashboard dashboard, CancellationToken ct) =>
        {
            var result = await dashboard.GetAsync(ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            var v = result.Value!;
            return Results.Ok(new ContractDashboardDto(
                v.Today,
                [.. v.Counts.Select(c => new DashboardCountDto(c.Key, c.Count))],
                [.. v.ByType.Select(c => new DashboardCountDto(c.Key, c.Count))],
                v.Money is null ? null : [.. v.Money.Select(m => new DashboardMoneyDto(m.Currency, m.Count, m.NetTotal, m.GrossTotal))],
                [.. v.Upcoming.Select(d => new DashboardDateDto(d.ContractId, d.Number, d.Title, d.Kind, d.Date, d.DaysLeft))],
                v.Truncated));
        }).RequirePermission(ContractsModule.Permissions.ContractsRead);

        group.MapGet("/contracts/export", ExportAsync).RequirePermission(ContractsModule.Permissions.ContractsExport);
    }

    /// <summary>
    /// CSV dışa aktarma. Süzgeçler liste uç noktasıyla aynıdır (kaydedilmiş görünüm de buraya aynı süzgeçle gelir). Tutar sütunları yalnızca tutar iznine sahip
    /// kullanıcıya yazılır (diğerlerinde sütun hiç yoktur). Müşteri adı ortak lookup'tan çözülür: görünmeyen kayıt "—" olur. Hücre başındaki formül karakterleri
    /// etkisizleştirilir. Çıkış denetim kaydına yazılır (kim, kaç kayıt, hangi süzgeç).
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpRequest http, string? search, IRecordPipeline pipeline, ICurrentAccess access, ILookupSearch lookups, ITenantContextAccessor tenant, IAuditWriter audit,
        IUnitOfWork unitOfWork, CancellationToken ct)
    {
        var filters = RecordEndpoints.FiltersFrom(http) ?? new Dictionary<string, string>();
        var items = new List<RecordData>();
        var truncated = false;
        for (var page = 1; items.Count < ExportMaxRows; page++)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(ContractsModule.Entities.Contract, page, RecordQuery.MaxPageSize, search, filters), ct);
            if (!result.IsSuccess)
            {
                return RecordEndpoints.ToProblem(result.Error!);
            }

            items.AddRange(result.Value!.Items);
            truncated = result.Value.TotalCount > items.Count;
            if (result.Value.Items.Count == 0 || !truncated)
            {
                break;
            }
        }

        var money = (await access.GetPermissionsAsync(ct)).Contains(ContractsModule.Permissions.FinancialsRead);
        var columns = ExportColumns.Where(c => money || !MoneyColumns.Contains(c)).ToArray();

        var partyIds = items.Select(i => Guid.TryParse(i.Values["partyId"]?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        for (var skip = 0; skip < partyIds.Count; skip += 100)
        {
            var resolved = await lookups.ResolveAsync(ContractsLookupDefinitions.ContractParty, partyIds.Skip(skip).Take(100).ToList(), ct);
            foreach (var item in resolved.Value ?? [])
            {
                names[item.Id] = item.Label ?? "—";
            }
        }

        if (tenant.Current is { } current)
        {
            audit.Add(new AuditEntry(current.TenantId, current.UserId, ContractsModule.Entities.Contract, Guid.Empty, AuditAction.Exported,
                new JsonObject { ["count"] = items.Count, ["truncated"] = truncated, ["money"] = money, ["filters"] = string.Join('&', filters.Select(f => $"{f.Key}={f.Value}")) },
                ExecutionSource.Api, null));
            await unitOfWork.SaveChangesAsync(ct);
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns));
        foreach (var item in items)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => LeadEndpoints.Csv(c == "customer"
                ? (Guid.TryParse(item.Values["partyId"]?.ToString(), out var id) && names.TryGetValue(id, out var name) ? name : "—")
                : item.Values[c]?.ToString()))));
        }

        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "sozlesmeler.csv");
    }
}
