using System.Globalization;
using Modulix.Api.Records;
using Modulix.Core.Contracts.Records;
using Modulix.Core.Contracts.Tenancy;
using Modulix.Core.Records;
using Modulix.Modules.Crm;
using Modulix.Shared.Crm;

namespace Modulix.Api.Crm;

/// <summary>
/// GET /api/crm/companies/{id}/summary (ADR-0010): firma detayının özetleri. Sayılar ve toplamlar kayıt boru hattından,
/// yani çağıranın izni, veri kapsamı ve alan güvenliğiyle hesaplanır; sayfa boyutuna bağlı değildir. Çağıranın okuma izni
/// olmayan bölüm <c>null</c> döner (sıfır değil). Tutar toplamları para birimi bazındadır ve tutar izni gerektirir.
/// </summary>
internal static class CompanySummaryEndpoint
{
    private static async Task<RecordResult<RecordPage>> QueryAsync(
        IRecordPipeline pipeline, string entityKey, Guid companyId, int page, int pageSize, params (string Name, string Value)[] filters)
    {
        var all = new Dictionary<string, string> { ["companyId"] = companyId.ToString() };
        foreach (var (name, value) in filters)
        {
            all[name] = value;
        }

        return await pipeline.QueryAsync(new RecordQuery(entityKey, page, pageSize, Filters: all), CancellationToken.None);
    }

    // Bölüm okunamıyorsa (izin/lisans) null; başka hata beklenmedikse istisna (500) — sessizce sıfır gösterilmez.
    private static bool Readable<T>(RecordResult<T> result) =>
        result.IsSuccess
            ? true
            : result.Error!.Code is RecordErrorCode.PermissionDenied or RecordErrorCode.FeatureNotLicensed
                ? false
                : throw new InvalidOperationException($"Özet hesaplanamadı: {result.Error.Message}");

    private static async Task<(int? Count, IReadOnlyList<CurrencyTotal>? Totals)> OpportunitiesAsync(
        IRecordPipeline pipeline, Guid companyId, string category, bool amountsVisible)
    {
        var first = await QueryAsync(pipeline, CrmModule.Entities.Opportunity, companyId, 1, 1, ("stageCategory", category));
        if (!Readable(first))
        {
            return (null, null);
        }

        var total = first.Value!.TotalCount;
        var sums = new SortedDictionary<string, (decimal Amount, int Count)>(StringComparer.Ordinal);
        for (var page = 1; (page - 1) * RecordQuery.MaxPageSize < total; page++)
        {
            var result = await QueryAsync(pipeline, CrmModule.Entities.Opportunity, companyId, page, RecordQuery.MaxPageSize, ("stageCategory", category));
            if (!Readable(result))
            {
                return (null, null);
            }

            foreach (var item in result.Value!.Items)
            {
                if (item.Values.TryGetPropertyValue("amount", out var amount) && amount is not null && item.Values["currency"]?.GetValue<string>() is { } currency)
                {
                    var (sum, count) = sums.GetValueOrDefault(currency);
                    sums[currency] = (sum + amount.GetValue<decimal>(), count + 1);
                }
            }
        }

        return (total, !amountsVisible ? null : [.. sums.Select(s => new CurrencyTotal(s.Key, s.Value.Amount, s.Value.Count))]);
    }

    public static async Task<IResult> GetAsync(
        Guid id, IRecordPipeline pipeline, IFieldAccessService fieldAccess, ITenantContextAccessor tenant, CancellationToken ct)
    {
        // Firma çağıranın kapsamında değilse özet de yoktur (404).
        var company = await pipeline.GetAsync(new RecordRef(CrmModule.Entities.Company, id), ct);
        if (!company.IsSuccess)
        {
            return RecordEndpoints.ToProblem(company.Error!);
        }

        // Tutar alanı hassastır (Y8b): izin yoksa toplamlar hiç verilmez (boş liste "sıfır" anlamına gelirdi).
        var hidden = await fieldAccess.GetHiddenFieldsAsync(tenant.Current!, CrmModule.Entities.Opportunity, ct);
        var amountsVisible = !hidden.Contains("amount");
        var (open, openTotals) = await OpportunitiesAsync(pipeline, id, CrmModule.StageCategories.Open, amountsVisible);
        var (won, wonTotals) = await OpportunitiesAsync(pipeline, id, CrmModule.StageCategories.Won, amountsVisible);

        int? openActivities = null;
        int? overdue = null;
        var activities = await QueryAsync(pipeline, CrmModule.Entities.Activity, id, 1, 1, ("open", "true"));
        if (Readable(activities))
        {
            openActivities = activities.Value!.TotalCount;
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            overdue = (await QueryAsync(pipeline, CrmModule.Entities.Activity, id, 1, 1, ("open", "true"), ("dueBefore", now))).Value!.TotalCount;
        }

        return Results.Ok(new CompanySummaryDto(open, won, openTotals, wonTotals, openActivities, overdue));
    }
}
