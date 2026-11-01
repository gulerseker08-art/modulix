using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;

namespace Modulix.Modules.Contracts.Lifecycle;

/// <summary>Panodaki bir sayaç: ne kadar sözleşme, ve tıklanınca listeyi açan görünüm anahtarı (<see cref="ContractViewRules.Presets"/> ya da null).</summary>
public sealed record DashboardCount(string Key, int Count);

/// <summary>Para birimi başına yürürlükteki sözleşme sayısı ve toplamlar (para birimleri kur olmadan toplanmaz).</summary>
public sealed record DashboardMoney(string Currency, int Count, decimal NetTotal, decimal GrossTotal);

/// <summary>Yaklaşan kritik tarih: sözleşme, hangi tarih (<c>notice</c> son bildirim, <c>end</c> bitiş), tarih ve kalan gün.</summary>
public sealed record DashboardDate(Guid ContractId, string Number, string Title, string Kind, DateOnly Date, int DaysLeft);

/// <summary>
/// Sözleşme panosu (ADR-0022, dilim 9): kullanıcının görebildiği sözleşmelerin sayaçları, türe göre dağılım, tutarlar (tutar izni olanlara; para birimi ayrı) ve yaklaşan tarihler.
/// <see cref="Truncated"/> doluysa türe/tutara/tarihe göre özetler yürürlükteki ilk <see cref="ContractDashboardService.ScanLimit"/> sözleşmeye dayanır.
/// </summary>
public sealed record ContractDashboardView(
    DateOnly Today,
    IReadOnlyList<DashboardCount> Counts,
    IReadOnlyList<DashboardCount> ByType,
    IReadOnlyList<DashboardMoney>? Money,
    IReadOnlyList<DashboardDate> Upcoming,
    bool Truncated);

public interface IContractDashboard
{
    Task<RecordResult<ContractDashboardView>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>Panonun sabitleri (sayaç anahtarları, tarama sınırı).</summary>
public static class ContractDashboardService
{
    public const int ScanLimit = 2000;

    public const int UpcomingLimit = 8;

    /// <summary>Panodaki sayaçların sırası; anahtarların bir kısmı liste hızlı görünümüyle aynıdır.</summary>
    public static IReadOnlyList<string> CountKeys { get; } =
        ["total", "drafts", "approval", "signature", "active", "awaitingStart", "ending", "notice", "overdue"];
}

internal sealed class ContractDashboard(IRecordPipeline pipeline, ICurrentAccess access, TimeProvider time, IContractPolicyProvider? policy = null) : IContractDashboard
{
    private const string Key = ContractsModule.Entities.Contract;

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public async Task<RecordResult<ContractDashboardView>> GetAsync(CancellationToken cancellationToken)
    {
        var settings = policy is null ? ContractPolicySettings.Default : await policy.GetAsync(cancellationToken);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var today = ContractPeriod.Today(time.GetUtcNow(), zone);
        var in30 = Date(today.AddDays(30));
        var in90 = Date(today.AddDays(90));

        // Sayaçlar liste ekranındaki görünümlerle aynı süzgeçleri kullanır; böylece panodaki sayı tıklanınca açılan listeyle tutar.
        var queries = new (string Key, Dictionary<string, string> Filters)[]
        {
            ("total", new()),
            ("drafts", new() { ["preparationStatus"] = PreparationStatus.Draft }),
            ("approval", new() { ["approvalStatus"] = ApprovalStatus.Pending }),
            ("signature", new() { ["signatureStatus"] = $"{SignatureStatus.Pending},{SignatureStatus.PartiallySigned}" }),
            ("active", new() { ["effectiveStatus"] = EffectiveStatus.Active }),
            ("awaitingStart", new() { ["effectiveStatus"] = EffectiveStatus.AwaitingStart }),
            ("ending", new() { ["effectiveStatus"] = EffectiveStatus.Active, ["endFrom"] = Date(today), ["endTo"] = in90 }),
            ("notice", new() { ["effectiveStatus"] = EffectiveStatus.Active, ["noticeFrom"] = Date(today), ["noticeTo"] = in30 }),
            ("overdue", new() { ["commitmentOverdue"] = "true" }),
        };

        var counts = new Dictionary<string, int>();
        foreach (var (key, filters) in queries)
        {
            // Aynı DbContext üzerinde sıralı okunur (paralel okuma desteklenmez).
            var result = await pipeline.QueryAsync(new RecordQuery(Key, 1, 1, null, filters), cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<ContractDashboardView>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }

            counts[key] = counts.GetValueOrDefault(key) + result.Value!.TotalCount;
        }

        var showMoney = (await access.GetPermissionsAsync(cancellationToken)).Contains(ContractsModule.Permissions.FinancialsRead);
        var rows = new List<RecordData>();
        var truncated = false;
        for (var page = 1; rows.Count < ContractDashboardService.ScanLimit; page++)
        {
            var result = await pipeline.QueryAsync(
                new RecordQuery(Key, page, RecordQuery.MaxPageSize, null, new Dictionary<string, string> { ["effectiveStatus"] = EffectiveStatus.Active }), cancellationToken);
            if (!result.IsSuccess)
            {
                return RecordResult<ContractDashboardView>.Fail(result.Error!.Code, result.Error.Message, result.Error.FieldErrors);
            }

            rows.AddRange(result.Value!.Items);
            truncated = result.Value.TotalCount > rows.Count;
            if (result.Value.Items.Count == 0 || !truncated)
            {
                break;
            }
        }

        var byType = rows
            .GroupBy(r => Text(r, "type") ?? "—")
            .Select(g => new DashboardCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<DashboardMoney>? money = showMoney
            ? [.. rows.GroupBy(r => Text(r, "currency") ?? "—").Select(g => new DashboardMoney(g.Key, g.Count(), g.Sum(r => Amount(r, "netTotal")), g.Sum(r => Amount(r, "grossTotal")))).OrderBy(m => m.Currency, StringComparer.Ordinal)]
            : null;

        var upcoming = new List<DashboardDate>();
        foreach (var r in rows)
        {
            var notice = DateOf(r, "noticeDeadline");
            var end = DateOf(r, "endDate");
            var indefinite = r.Values["isIndefinite"]?.GetValue<bool>() ?? false;
            // Hem son bildirim tarihi hem bitiş listelenir (belirsiz süreli sözleşmede ikisi de yoktur): bildirim süresi geçse de bitişi yaklaşan sözleşme görünür kalır.
            foreach (var (kind, date) in new (string, DateOnly?)[] { ("notice", indefinite ? null : notice), ("end", indefinite ? null : end) })
            {
                if (date is { } d && d >= today && d <= today.AddDays(90))
                {
                    upcoming.Add(new DashboardDate(r.Id, Text(r, "number") ?? string.Empty, Text(r, "title") ?? string.Empty, kind, d, d.DayNumber - today.DayNumber));
                }
            }
        }

        var view = new ContractDashboardView(
            today,
            [.. ContractDashboardService.CountKeys.Select(k => new DashboardCount(k, counts.GetValueOrDefault(k)))],
            byType,
            money,
            [.. upcoming.OrderBy(d => d.Date).ThenBy(d => d.Number, StringComparer.Ordinal).Take(ContractDashboardService.UpcomingLimit)],
            truncated);
        return RecordResult<ContractDashboardView>.Ok(view);
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", Culture);

    private static string? Text(RecordData row, string field) => row.Values[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal Amount(RecordData row, string field) => row.Values[field] is { } node && decimal.TryParse(node.ToString(), NumberStyles.Number, Culture, out var value) ? value : 0m;

    private static DateOnly? DateOf(RecordData row, string field) => Text(row, field) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", Culture, DateTimeStyles.None, out var d) ? d : null;
}
