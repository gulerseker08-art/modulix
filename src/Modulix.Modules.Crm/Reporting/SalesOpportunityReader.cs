using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;

namespace Modulix.Modules.Crm.Reporting;

/// <summary>
/// Satış raporlama politikası (tenant ayarlarından): firmanın saat dilimi, mali yıl başlangıç ayı, tahmin kategori eşikleri ve varsayılan raporlama para birimi.
/// Modül ayar okuyamaz; sağlayıcıyı API verir (<see cref="Opportunities.IOpportunityPolicyProvider"/> ile aynı kalıp).
/// </summary>
public sealed record SalesReportingPolicy(TimeZoneInfo TimeZone, int FiscalStartMonth, ForecastPolicy Forecast, string DefaultCurrency)
{
    public static SalesReportingPolicy Default { get; } = new(
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"), ReportPeriods.DefaultFiscalStartMonth, ForecastPolicy.Default, Opportunity.DefaultCurrency);
}

public interface ISalesReportingPolicyProvider
{
    Task<SalesReportingPolicy> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Rapor için okunan fırsat satırı (kayıt boru hattından; izin, kapsam ve hassas alan kuralları uygulanmıştır).</summary>
internal sealed record OpportunityRow(
    Guid Id, string Number, string Title, Guid? OwnerId, string Status, string Stage, decimal? Amount, string Currency, int? Probability,
    bool ProbabilityManual, DateOnly? ExpectedCloseDate, DateOnly? ActualCloseDate, decimal? WonAmount, string? WonCurrency, DateTimeOffset? LastInteractionAt = null)
{
    public OpportunityFact ToFact() => new(Id, Status, Amount, Currency, Probability, ExpectedCloseDate, ActualCloseDate, WonAmount, WonCurrency);
}

internal sealed record OpportunityPage(List<OpportunityRow> Rows, bool Truncated, RecordError? Error);

/// <summary>Herhangi bir kayıt türünün sayfalanarak okunmuş ham kayıtları.</summary>
internal sealed record RawPage(List<RecordData> Rows, bool Truncated, RecordError? Error);

/// <summary>Satış raporlarının fırsat okuyucusu: yalnızca <see cref="IRecordPipeline"/> üzerinden, sayfalayarak ve satır sınırıyla okur.</summary>
internal sealed class SalesOpportunityReader(IRecordPipeline pipeline)
{
    /// <summary>Bir okumada en fazla fırsat; aşılırsa sonuç "kesildi" olarak işaretlenir (sessizce eksik toplam sunulmaz).</summary>
    public const int MaxRows = 5000;

    public async Task<OpportunityPage> ReadAsync(IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var raw = await ReadRawAsync(CrmModule.Entities.Opportunity, filters, cancellationToken);
        return new OpportunityPage([.. raw.Rows.Select(ToRow)], raw.Truncated, raw.Error);
    }

    /// <summary>Kayıt türünün kayıtlarını boru hattından (izin, veri kapsamı, hassas alan kuralları uygulanmış) sayfalayarak okur; satır sınırı aşılırsa <c>Truncated</c>.</summary>
    public async Task<RawPage> ReadRawAsync(string entityKey, IReadOnlyDictionary<string, string> filters, CancellationToken cancellationToken)
    {
        var rows = new List<RecordData>();
        var pageNumber = 1;
        while (rows.Count < MaxRows)
        {
            var result = await pipeline.QueryAsync(new RecordQuery(entityKey, pageNumber, RecordQuery.MaxBulkPageSize, null, filters) { Bulk = true }, cancellationToken);
            if (!result.IsSuccess)
            {
                return new RawPage([], false, result.Error);
            }

            rows.AddRange(result.Value!.Items);
            if (result.Value.Items.Count == 0 || rows.Count >= result.Value.TotalCount)
            {
                return new RawPage(rows, false, null);
            }

            pageNumber++;
        }

        return new RawPage(rows, true, null);
    }

    public static Dictionary<string, string> Filters(Guid? ownerId, params string[] pairs)
    {
        var filters = new Dictionary<string, string>();
        if (ownerId is { } owner)
        {
            filters["ownerId"] = owner.ToString();
        }

        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            filters[pairs[i]] = pairs[i + 1];
        }

        return filters;
    }

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static OpportunityRow ToRow(RecordData record)
    {
        var v = record.Values;
        string? Text(string name) => v[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        decimal? Money(string name) => v[name] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : null;
        int? Int(string name) => v[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
        Guid? Id(string name) => Guid.TryParse(Text(name), out var guid) ? guid : null;
        DateOnly? Date(string name) => DateOnly.TryParseExact(Text(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        return new OpportunityRow(
            record.Id, Text("opportunityNumber") ?? string.Empty, Text("title") ?? string.Empty, Id("ownerId"), Text("status") ?? OpportunityStatus.Open,
            Text("stage") ?? string.Empty, Money("amount"), Text("currency") ?? Opportunity.DefaultCurrency, Int("probability"),
            v["probabilityManual"] is JsonValue manual && manual.TryGetValue<bool>(out var flag) && flag,
            Date("expectedCloseDate"), Date("actualCloseDate"), Money("wonAmount"), Text("wonCurrency"),
            DateTimeOffset.TryParse(Text("lastInteractionAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var interaction) ? interaction : null);
    }
}
