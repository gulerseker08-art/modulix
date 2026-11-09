using System.Globalization;
using System.Text.Json.Nodes;
using Modulix.Core.Contracts.Authorization;
using Modulix.Core.Contracts.Records;
using Modulix.Modules.Crm.Domain;
using Modulix.Modules.Crm.Opportunities;
using Modulix.Modules.Crm.Quotes;

namespace Modulix.Modules.Crm.Reporting;

/// <summary>Kural tabanlı öneri türleri (yapay zekâ değil; kurallar <see cref="MyDayRules"/>'tadır).</summary>
public static class MyDaySuggestionKind
{
    public const string AcceptedQuote = "AcceptedQuote";
    public const string NewLead = "NewLead";
    public const string OverdueOpportunity = "OverdueOpportunity";
    public const string QuoteFollowUp = "QuoteFollowUp";
    public const string StaleOpportunity = "StaleOpportunity";

    /// <summary>Öncelik sırası: her türün en önemlisi bu sırayla dizilir, sonra ikinciler (<see cref="MyDayRules.Suggest"/>).</summary>
    public static IReadOnlyList<string> Priority { get; } = [AcceptedQuote, NewLead, OverdueOpportunity, QuoteFollowUp, StaleOpportunity];
}

public sealed record MyDayOpportunity(
    Guid Id, string Number, string Title, string Stage, decimal? Amount, string Currency, int? Probability, DateOnly? ExpectedCloseDate,
    DateTimeOffset? LastInteractionAt, DateTimeOffset? StageEnteredAt);

public sealed record MyDayLead(Guid Id, string Number, string Name, string? CompanyName, string? Source, DateTimeOffset ReceivedAt);

public sealed record MyDayQuote(Guid Id, string Number, string Title, string? Customer, decimal? NetTotal, string Currency, DateTimeOffset PresentedAt);

/// <summary>Müşterinin kabul ettiği teklif ve bağlı olduğu fırsat (<see cref="DecidedAt"/> kabul anı).</summary>
public sealed record MyDayAcceptedQuote(Guid Id, string Number, Guid OpportunityId, decimal? NetTotal, string Currency, DateTimeOffset DecidedAt);

/// <summary>
/// Sıradaki adım önerisi. Metni istemci türe göre kurar; sunucu yalnızca kaydı ve gerekçe sayılarını verir: <see cref="DaysOverdue"/> kapanış tarihinden bu yana,
/// <see cref="DaysSinceContact"/> son temastan bu yana, <see cref="DaysWaiting"/> teklifin sunulmasından (kabul edilen teklifte kabulden) bu yana geçen gün;
/// <see cref="Since"/> adayın geliş ya da teklifin kabul anı. Kabul edilen teklif önerisinin kaydı fırsattır; <see cref="Subtitle"/> teklif numarası, tutar teklifin net toplamıdır.
/// </summary>
public sealed record MyDaySuggestion(
    string Kind, Guid RecordId, string Number, string Title, string? Subtitle, string? Stage, decimal? Amount, string Currency,
    int? DaysOverdue, int? DaysSinceContact, int? DaysWaiting, DateTimeOffset? Since);

public sealed record MyDayClosing(Guid Id, string Number, string Title, decimal? Amount, string Currency, int? Probability, DateOnly ExpectedCloseDate, bool Overdue);

/// <summary>
/// "Benim günüm" özeti: oturumdaki kullanıcının kendi kayıtları. Bölümler izne bağlıdır: izni olmayan bölüm boş döner ve <c>…Available</c> false olur.
/// Adetler: <see cref="OverdueOpportunityCount"/> kapanış tarihi geçmiş açık fırsat, <see cref="NewLeadCount"/> ilk teması bekleyen yeni aday; hafta pazartesi başlar.
/// </summary>
public sealed record MyDayResult(
    DateOnly Today,
    DateOnly WeekStart,
    bool AmountsVisible,
    bool OpportunitiesAvailable,
    bool LeadsAvailable,
    bool QuotesAvailable,
    int OpenOpportunityCount,
    int OverdueOpportunityCount,
    int NewLeadCount,
    int QuotesPresentedThisWeek,
    int WonThisWeek,
    int QuoteReminderDays,
    int StaleDays,
    IReadOnlyList<MyDaySuggestion> Suggestions,
    IReadOnlyList<MyDayClosing> ClosingThisMonth);

/// <summary>Öneri ve "bu ay kapanabilecekler" kuralları; saf fonksiyonlar (birim testli).</summary>
public static class MyDayRules
{
    public const int SuggestionLimit = 5;
    public const int ClosingLimit = 5;

    /// <summary>Firma ayarı okunamazsa: sunulan teklif bu kadar gün yanıtsız kalınca hatırlatma önerilir (<c>crm.quote.reply-reminder-days</c>; 0 kapalı).</summary>
    public const int DefaultQuoteReminderDays = 3;

    /// <summary>
    /// Öneriler: kabul edilen teklifi olan açık fırsat (en eski kabul önce), yeni aday (en uzun bekleyen önce), kapanış tarihi geçmiş fırsat (en büyük tutar önce),
    /// yanıtsız teklif (en eski önce), temassız fırsat (en büyük tutar önce). Bir türün listeyi doldurmaması için türler sırayla birer birer alınır; en fazla
    /// <see cref="SuggestionLimit"/>. Kabul edilen teklifi olan fırsat başka türde ayrıca önerilmez. Eşikler firma ayarıdır: <paramref name="quoteReminderDays"/>
    /// teklif hatırlatma (0 kapalı), <paramref name="staleDays"/> fırsat hareketsizlik eşiği (temas yoksa aşamaya girişten sayılır). Gün farkları firmanın
    /// saat dilimindeki takvim günüdür.
    /// </summary>
    public static IReadOnlyList<MyDaySuggestion> Suggest(
        IEnumerable<MyDayOpportunity> opportunities, IEnumerable<MyDayLead> leads, IEnumerable<MyDayQuote> quotes, DateTimeOffset now, TimeZoneInfo zone,
        IEnumerable<MyDayAcceptedQuote>? acceptedQuotes = null, int quoteReminderDays = DefaultQuoteReminderDays, int staleDays = OpportunityPolicy.DefaultStaleDays)
    {
        var today = ReportPeriods.Today(now, zone);
        int DaysSince(DateTimeOffset at) => Math.Max(0, today.DayNumber - ReportPeriods.Today(at, zone).DayNumber);
        var all = opportunities.ToList();
        var byId = all.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());

        // Kabul edilen teklifin fırsatı hâlâ açıksa kazanıldı olarak kapatılmalıdır; aynı fırsatın birden çok kabul edilen teklifinden en yenisi gösterilir.
        var accepted = (acceptedQuotes ?? [])
            .Where(q => byId.ContainsKey(q.OpportunityId))
            .GroupBy(q => q.OpportunityId)
            .Select(g => g.OrderByDescending(q => q.DecidedAt).First())
            .OrderBy(q => q.DecidedAt).ThenBy(q => q.Number, StringComparer.Ordinal)
            .Select(q =>
            {
                MyDayOpportunity o = byId[q.OpportunityId];
                return new MyDaySuggestion(MyDaySuggestionKind.AcceptedQuote, o.Id, o.Number, o.Title, q.Number, o.Stage, q.NetTotal, q.Currency,
                    null, null, DaysSince(q.DecidedAt), q.DecidedAt);
            })
            .ToList();
        HashSet<Guid> handled = [.. accepted.Select(a => a.RecordId)];
        var open = all.Where(o => !handled.Contains(o.Id)).ToList();

        var newLeads = leads.OrderBy(l => l.ReceivedAt).ThenBy(l => l.Number, StringComparer.Ordinal)
            .Select(l => new MyDaySuggestion(MyDaySuggestionKind.NewLead, l.Id, l.Number, l.Name, l.CompanyName, null, null, Opportunity.DefaultCurrency,
                null, null, null, l.ReceivedAt));

        var overdue = open.Where(o => o.ExpectedCloseDate is { } close && close < today)
            .OrderByDescending(o => o.Amount ?? -1m).ThenBy(o => o.ExpectedCloseDate).ThenBy(o => o.Number, StringComparer.Ordinal)
            .Select(o => new MyDaySuggestion(MyDaySuggestionKind.OverdueOpportunity, o.Id, o.Number, o.Title, null, o.Stage, o.Amount, o.Currency,
                today.DayNumber - o.ExpectedCloseDate!.Value.DayNumber, o.LastInteractionAt is { } last ? DaysSince(last) : null, null, null));

        var waitingQuotes = quotes.Where(q => quoteReminderDays > 0 && DaysSince(q.PresentedAt) >= quoteReminderDays)
            .OrderBy(q => q.PresentedAt).ThenBy(q => q.Number, StringComparer.Ordinal)
            .Select(q => new MyDaySuggestion(MyDaySuggestionKind.QuoteFollowUp, q.Id, q.Number, q.Title, q.Customer, null, q.NetTotal, q.Currency,
                null, null, DaysSince(q.PresentedAt), q.PresentedAt));

        var stale = open.Where(o => o.ExpectedCloseDate is not { } close || close >= today)
            .Select(o => (Opportunity: o, Days: (o.LastInteractionAt ?? o.StageEnteredAt) is { } since ? DaysSince(since) : (int?)null))
            .Where(x => staleDays > 0 && x.Days >= staleDays)
            .OrderByDescending(x => x.Opportunity.Amount ?? -1m).ThenByDescending(x => x.Days).ThenBy(x => x.Opportunity.Number, StringComparer.Ordinal)
            .Select(x => new MyDaySuggestion(MyDaySuggestionKind.StaleOpportunity, x.Opportunity.Id, x.Opportunity.Number, x.Opportunity.Title, null,
                x.Opportunity.Stage, x.Opportunity.Amount, x.Opportunity.Currency, null, x.Days, null, null));

        var queues = new[] { accepted, newLeads, overdue, waitingQuotes, stale }.Select(q => new Queue<MyDaySuggestion>(q)).ToList();
        var result = new List<MyDaySuggestion>();
        while (result.Count < SuggestionLimit && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues.Where(q => q.Count > 0))
            {
                if (result.Count == SuggestionLimit)
                {
                    break;
                }

                result.Add(queue.Dequeue());
            }
        }

        return result;
    }

    /// <summary>Beklenen kapanışı bu takvim ayında olan açık fırsatlar (bugünden önceyse "gecikti"); olasılık, sonra tutar azalan; en fazla <see cref="ClosingLimit"/>.</summary>
    public static IReadOnlyList<MyDayClosing> ClosingThisMonth(IEnumerable<MyDayOpportunity> opportunities, DateOnly today)
    {
        var start = new DateOnly(today.Year, today.Month, 1);
        var end = start.AddMonths(1);
        return [.. opportunities
            .Where(o => o.ExpectedCloseDate is { } close && close >= start && close < end)
            .OrderByDescending(o => o.Probability ?? -1).ThenByDescending(o => o.Amount ?? -1m).ThenBy(o => o.Number, StringComparer.Ordinal)
            .Take(ClosingLimit)
            .Select(o => new MyDayClosing(o.Id, o.Number, o.Title, o.Amount, o.Currency, o.Probability, o.ExpectedCloseDate!.Value, o.ExpectedCloseDate < today))];
    }

    /// <summary>Haftanın ilk günü (pazartesi).</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

public interface IMyDay
{
    Task<RecordResult<MyDayResult>> BuildAsync(CancellationToken cancellationToken);
}

/// <summary>
/// "Benim günüm" (CRM panosu). Yalnızca oturumdaki kullanıcının sahibi olduğu kayıtlar okunur (<c>ownerId=me</c>) ve hepsi kayıt boru hattından geçer:
/// izin, veri kapsamı ve hassas alan kuralları liste ekranlarıyla aynıdır. Tutar izni yoksa tutar ve olasılık dönmez.
/// </summary>
internal sealed class MyDay(
    SalesOpportunityReader reader, ICurrentAccess access, TimeProvider time, ISalesReportingPolicyProvider? policyProvider = null,
    IOpportunityPolicyProvider? opportunityPolicy = null, IQuotePolicyProvider? quotePolicy = null) : IMyDay
{
    private const string Me = "me";

    /// <summary>Kabul edilen teklif önerisi için geriye bakılan gün (eski kabuller panoyu doldurmaz).</summary>
    private const int AcceptedLookbackDays = 90;

    public async Task<RecordResult<MyDayResult>> BuildAsync(CancellationToken cancellationToken)
    {
        var policy = policyProvider is null ? SalesReportingPolicy.Default : await policyProvider.GetAsync(cancellationToken);
        var staleDays = opportunityPolicy is null ? OpportunityPolicy.DefaultStaleDays : (await opportunityPolicy.GetAsync(cancellationToken)).StaleDays;
        var quoteReminderDays = quotePolicy is null ? MyDayRules.DefaultQuoteReminderDays : (await quotePolicy.GetAsync(cancellationToken)).ReplyReminderDays;
        var now = time.GetUtcNow();
        var today = ReportPeriods.Today(now, policy.TimeZone);
        var weekStart = MyDayRules.WeekStart(today);
        var weekStartUtc = ReportPeriods.StartOfDayUtc(weekStart, policy.TimeZone);

        var canOpportunities = await access.HasPermissionAsync(CrmModule.Permissions.OpportunitiesRead, cancellationToken);
        var canLeads = await access.HasPermissionAsync(CrmModule.Permissions.LeadsRead, cancellationToken);
        var canQuotes = await access.HasPermissionAsync(CrmModule.Permissions.QuotesRead, cancellationToken);
        var amountsVisible = canOpportunities && await access.HasPermissionAsync(CrmModule.Permissions.OpportunityAmountsRead, cancellationToken);

        List<MyDayOpportunity> open = [];
        var wonThisWeek = 0;
        if (canOpportunities)
        {
            var page = await reader.ReadAsync(SalesOpportunityReader.Filters(null, "ownerId", Me, "status", OpportunityStatus.Open), cancellationToken);
            var won = await reader.ReadAsync(SalesOpportunityReader.Filters(
                null, "ownerId", Me, "status", OpportunityStatus.Won, "actualCloseFrom", SalesOpportunityReader.Iso(weekStart),
                "actualCloseTo", SalesOpportunityReader.Iso(today)), cancellationToken);
            if ((page.Error ?? won.Error) is { } error)
            {
                return Fail(error);
            }

            open = [.. page.Rows.Select(r => new MyDayOpportunity(
                r.Id, r.Number, r.Title, r.Stage, amountsVisible ? r.Amount : null, r.Currency, amountsVisible ? r.Probability : null, r.ExpectedCloseDate,
                r.LastInteractionAt, r.StageEnteredAt))];
            wonThisWeek = won.Rows.Count;
        }

        List<MyDayLead> leads = [];
        if (canLeads)
        {
            var page = await reader.ReadRawAsync(CrmModule.Entities.Lead, new Dictionary<string, string> { ["ownerId"] = Me, ["status"] = LeadStatus.New }, cancellationToken);
            if (page.Error is { } error)
            {
                return Fail(error);
            }

            leads = [.. page.Rows.Select(ToLead).Where(l => l is not null).Select(l => l!)];
        }

        List<MyDayQuote> quotes = [];
        List<MyDayAcceptedQuote> accepted = [];
        var presentedThisWeek = 0;
        if (canQuotes)
        {
            var waiting = await reader.ReadRawAsync(CrmModule.Entities.Quote,
                new Dictionary<string, string> { ["ownerId"] = Me, ["status"] = QuoteStatus.Presented, ["latestOnly"] = "true" }, cancellationToken);
            var week = await reader.ReadRawAsync(CrmModule.Entities.Quote,
                new Dictionary<string, string> { ["ownerId"] = Me, ["presentedFrom"] = SalesOpportunityReader.Iso(weekStart.AddDays(-1)) }, cancellationToken);
            // Kabul edilmiş ama fırsatı hâlâ açık teklifler: yalnızca açık fırsatı olan kullanıcıda ve son kararlardan okunur.
            var acceptedPage = open.Count > 0
                ? await reader.ReadRawAsync(CrmModule.Entities.Quote,
                    new Dictionary<string, string>
                    {
                        ["ownerId"] = Me, ["status"] = QuoteStatus.Accepted, ["decidedFrom"] = SalesOpportunityReader.Iso(today.AddDays(-AcceptedLookbackDays)),
                    },
                    cancellationToken)
                : null;
            if ((waiting.Error ?? week.Error ?? acceptedPage?.Error) is { } error)
            {
                return Fail(error);
            }

            quotes = [.. waiting.Rows.Select(ToQuote).Where(q => q is not null).Select(q => q!)];
            accepted = [.. (acceptedPage?.Rows ?? []).Select(r => ToAcceptedQuote(r, amountsVisible)).Where(q => q is not null).Select(q => q!)];
            presentedThisWeek = week.Rows.Count(r => Time(r.Values, "presentedAt") is { } at && at >= weekStartUtc);
        }

        return RecordResult<MyDayResult>.Ok(new MyDayResult(
            today, weekStart, amountsVisible, canOpportunities, canLeads, canQuotes,
            open.Count, open.Count(o => o.ExpectedCloseDate < today), leads.Count, presentedThisWeek, wonThisWeek, quoteReminderDays, staleDays,
            MyDayRules.Suggest(open, leads, quotes, now, policy.TimeZone, accepted, quoteReminderDays, staleDays), MyDayRules.ClosingThisMonth(open, today)));
    }

    private static RecordResult<MyDayResult> Fail(RecordError error) => RecordResult<MyDayResult>.Fail(error.Code, error.Message, error.FieldErrors);

    private static string? Text(JsonObject values, string name) => values[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset? Time(JsonObject values, string name) =>
        DateTimeOffset.TryParse(Text(values, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static MyDayLead? ToLead(RecordData record)
    {
        var v = record.Values;
        if (Time(v, "firstReceivedAt") is not { } received || Time(v, "firstContactAt") is not null)
        {
            return null;
        }

        var person = string.Join(' ', new[] { Text(v, "firstName"), Text(v, "lastName") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var name = person.Length > 0 ? person : Text(v, "subject") ?? Text(v, "companyName") ?? string.Empty;
        return new MyDayLead(record.Id, Text(v, "leadNumber") ?? string.Empty, name, Text(v, "companyName"), Text(v, "source"), received);
    }

    private static MyDayAcceptedQuote? ToAcceptedQuote(RecordData record, bool amountsVisible)
    {
        var v = record.Values;
        if (Time(v, "decidedAt") is not { } decided || !Guid.TryParse(Text(v, "opportunityId"), out var opportunityId))
        {
            return null;
        }

        decimal? total = amountsVisible && v["netTotal"] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : null;
        return new MyDayAcceptedQuote(record.Id, Text(v, "quoteNumber") ?? string.Empty, opportunityId, total, Text(v, "currency") ?? Opportunity.DefaultCurrency, decided);
    }

    private static MyDayQuote? ToQuote(RecordData record)
    {
        var v = record.Values;
        if (Time(v, "presentedAt") is not { } presented)
        {
            return null;
        }

        decimal? total = v["netTotal"] is JsonValue value && value.TryGetValue<decimal>(out var amount) ? amount : null;
        return new MyDayQuote(record.Id, Text(v, "quoteNumber") ?? string.Empty, Text(v, "title") ?? string.Empty, Text(v, "customerNameSnapshot"),
            total, Text(v, "currency") ?? Opportunity.DefaultCurrency, presented);
    }
}
